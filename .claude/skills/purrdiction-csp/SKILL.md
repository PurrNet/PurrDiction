---
name: purrdiction-csp
description: Client-Side Prediction rules for PurrDiction — determinism, state design, side-effect dispatch, and reconciliation. Use when writing or reviewing code inside `Simulate`, `LateSimulate`, `StateSimulate`, spawning predicted prefabs, designing `IPredictedData` structs, dispatching VFX/audio/animation from simulation code, or debugging desync/rollback issues. Triggers on `PredictedRandom`, `IPredictedData`, `Simulate`, `LateSimulate`, `PredictedEvent`, `predictionManager.isSimulating`, `isReplaying`, `isVerified`, `cachedIsServer`, `PredictedHierarchy`, `PredictedIdentity`, "deterministic", "desync", "rollback", "reconciliation", "ghost projectile", "Failed to rollback".
---

# PurrDiction CSP — Determinism, State & Side-Effects

Same code runs on every client AND the server, every tick, possibly multiple times per tick during reconciliation replay. The simulation must produce **identical results** for identical inputs. Anything that varies across machines or across replays of the same tick is a desync source.

## 1. Forbidden in simulation code

These cause desync when used inside `Simulate`, `LateSimulate`, `StateSimulate`, or any code reachable from them.

| Forbidden | Why | Use instead |
|---|---|---|
| `UnityEngine.Random` / unseeded RNG | Different state per machine/replay | `PredictedRandom` on state struct |
| `Time.deltaTime` / `Time.time` | Variable per client | `delta` parameter in Simulate |
| `UnityEngine.Input.*` | Reads local input on all machines | The `INPUT` struct passed to Simulate |
| `GetComponent*` per tick | Allocation + cost; can return different instance post-respawn | Cache in `Awake` |
| `Coroutine` / `yield return` / `WaitForSeconds` | Frame-pacing dependent, unreplayable | Timer in state, or events/callbacks |
| `transform.position` **writes** when using `PredictedRigidbody2D` | Framework owns position via reconciliation | Apply forces or set `rb.velocity`/`rb.linearVelocity` |
| Static mutable state read by sim | Not reconciled; one peer's mutation won't replay | Promote to predicted state or treat as read-only config |

> **Read-vs-write nuance**: Reading `transform.position` inside Simulate is equivalent to `rb.position` because `PredictedTransform` writes both atomically and `Physics2D.SyncTransforms()` runs after every rollback. Writes are forbidden — only the framework sets position.

## 2. PredictedRandom

Lives on the state struct. Advances with each call; that advancement reconciles on rollback.

```csharp
public struct MyState : IPredictedData<MyState>
{
    public PredictedRandom random;
}

// Inside Simulate:
float roll = state.random.NextFloat(0f, 1f);
```

**Seeding** — must be deterministic and **non-zero** (xorshift RNG forbids zero).

| OK | Source |
|---|---|
| ✅ | `(uint)id.objectId.instanceId.value` (PredictedComponentID) |
| ✅ | XOR of position/name hashes (stable at spawn time) |
| ✅ | `predictionManager.sessionSeed` (shared across all peers) |
| ❌ | `gameObject.GetInstanceID()` (differs per machine) |
| ❌ | `DateTime.Now`, `Guid.NewGuid()`, `UnityEngine.Random` |

## 3. Input collection callbacks

The forbidden-list ban applies only to **simulation code**. PurrDiction provides explicit callbacks for input:

| Callback | When | Input.* allowed? |
|---|---|---|
| `UpdateInput(ref input)` | Every render frame, owner only | ✅ Yes — the designated place |
| `GetFinalInput(ref input)` | Once per tick, owner only | ✅ Final touch-up |
| `SanitizeInput(ref input)` | Once per tick, both client and server | ❌ Only validate the struct |
| `ModifyExtrapolatedInput(ref input)` | When extrapolating non-owner inputs | ❌ Strip non-continuous fields |
| `Simulate(input, ref state, delta)` | Every tick + every replay | ❌ **Never** |

Cardinal rule: inputs cross the wire as data. Once simulation begins, the only input source is the `INPUT` struct.

## 4. Time-based actions — quantize to ticks

Store timers as `float` in state, increment by `delta`, compare to a duration.

```csharp
state.timer += delta;
if (state.timer >= duration)
{
    /* fire */
    state.timer = 0f;
}
```

Never `Time.time` comparisons. Never manual `int counter++` tick counting — floats ride the same reconciliation pipe as everything else.

## 5. State design — derive over migrate

**Before adding a field to `IPredictedData`, ask: can the value be derived from existing reconciled state at point of use?**

Smaller state = less bandwidth, less reconciliation surface, fewer rollback bugs.

### Derivable (do NOT add to state)

| Temptation | Derive from |
|---|---|
| Cached reference to another component | Inject via Awake/constructor |
| Config value from ScriptableObject | Read SO each call |
| Boolean "has event happened" | A monotonic counter already in state |
| Constant layer mask | A `const` field |

### Must reconcile (add to state)

- A value chosen by `PredictedRandom` — the random advance must be in state
- A snapshot value whose meaning depends on the tick it was set (e.g., "speed at dash start")
- An accumulator (combo count, charge level) no other state captures

## 6. Side-effect taxonomy — gate by SUBSCRIBER, not by event

The decision to suppress with `!isReplaying` depends on **what the subscriber does**, not the event name.

| Subscriber writes to… | Guard | Why |
|---|---|---|
| `IPredictedData` state | **None** | Must replay for determinism |
| Owner-only effect (UI, rumble) | `!isReplaying && IsOwner()` | Owner sees once on live tick |
| Observer-visible effect (VFX, audio, animation) | **`PredictedEvent`** | Fires once per context correctly |
| Server-side ledger (analytics, scores) | `cachedIsServer && !isReplaying` | Single source of truth |

### PredictedEvent — how it works (verified from source)

```
Invoke():
  if server → always fire
  if owner → fire only when !isReplaying
  if non-owner → fire only when isVerified
```

Use `PredictedEvent` for sim→view crossing. Use plain method calls / `Action` for sim→sim work. The bug isn't "wrong dispatch mechanism" — it's "view-tier work inside a sim-tier callback." Fix by migrating the work, not the dispatch.

### Hand-rolled equivalent (when PredictedEvent migration is too costly)

```csharp
bool shouldFire = predictionManager.cachedIsServer
    ? true
    : identity.IsOwner()
        ? !predictionManager.isReplaying
        : predictionManager.isVerified;
```

## 7. Mutating state from external APIs

When a non-`PredictedIdentity` script needs to push a state change (damage system, pickup, etc.):

```csharp
public void TakeDamage(int amount)
{
    if (!predictionManager.isSimulating) return;
    ref var state = ref currentState;
    state.health = Mathf.Max(0, state.health - amount);
}
```

The `isSimulating` guard drops calls from `UpdateView`, button handlers, or off-tick callbacks — correct because state mutated outside the sim pass cannot reconcile.

## 8. Predicted spawn — exact parameter equality

`InstanceDetails.Equals` uses `Vector3.Equals` and `Quaternion.Equals` (exact, not approximate). Position/rotation passed to `hierarchy.Create(prefab, pos, rot)` must be **identical** between client and server calls.

Sub-pixel drift triggers destroy-and-recreate during reconciliation, resetting predicted state → visible as "ghost pop."

✅ Spawn from authoritative values: `rb.position + staticOffset` (rb is reconciled, offset is constant).
❌ Interpolated transforms, `_graphics` child positions, `Vector2.Lerp(...)` on spawn params.

## 9. StateSimulate write-back

For `PredictedStateNode<T>.StateSimulate`: the framework copies `currentState` to a local, calls your override, writes it back. **Direct writes to `currentState` inside StateSimulate are silently overwritten.** Always mutate only the `ref state` parameter.

## 10. History capacity

- **State history**: `tickRate × 10` entries (300 at 30Hz)
- **Input history**: `tickRate × 5` entries (150 at 30Hz)

If rollback depth exceeds state history, the framework logs `"Failed to rollback to tick X"` and falls back to `default(STATE)` — silently corrupting the entity. Treat this log as a defect.

## 11. Cross-platform determinism

For single-platform builds (same Unity version, same architecture), float math + the rules above is sufficient — reconciliation papers over residual rounding.

For cross-platform (IL2CPP vs Mono, ARM vs x86), PurrDiction ships:
- **FixedPoint** (`Runtime/FixedPoint/`): `FP`, `FPVec2`, `FPVec3`, `FPVec4`, `FPQuat`
- **SoftFloat** (`Runtime/SoftFloat/`): `sfloat`, `SVec2`, `SVec3`, `SVec4`, `SQuat`

Reach for these only after measuring determinism failures that survive the basic rules.

## 12. Prediction lifecycle (reference diagram)

```
                  ┌────────────── per tick (fixed timestep) ──────────────┐
UpdateInput ──►   │  Simulate(input, ref state, delta)                    │
(every frame,     │           ↓                                           │
 owner only)      │  LateSimulate(ref state, delta)   [post-physics]      │
                  │           ↓                                           │
                  │  SaveState(tick) ──► history buffer                   │
                  └───────────────────────────────────────────────────────┘
                            ↑ on misprediction
                  Rollback(tick) → re-Simulate forward to current

UpdateView(state, verified?) ── every render frame, never replays
LateUpdateView(delta)         ── every render frame, post-UpdateView
```

**Two tiers:**
- **Simulation** (`Simulate`, `LateSimulate`, `StateSimulate`): replays during rollback. Reconciled state mutations only.
- **View** (`UpdateView`, `LateUpdateView`): never replays. Animations, particles, camera, interpolated visuals.

Bugs come from putting view-tier work in sim callbacks (fires every replay) or sim-tier work in view callbacks (won't reconcile).

---

For detailed tick execution model with source-verified flag states, see [references/tick-execution-model.md](references/tick-execution-model.md).

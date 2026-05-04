# Tick Execution Model — Source-Verified

These are line-by-line readings of PurrDiction source (`Runtime/Core/PredictionManager.cs`, `PredictedIdentityStatefull.cs`, `PredictedIdentityWithInput.cs`). Trust them when reasoning about who-runs-what during a tick.

## Live tick — every peer

`PredictionManager.OnPreTick` runs on every peer (server + every connected client):

```
OnPreTick:
  cachedIsServer = isServer
  isSimulating = true
  if cachedIsServer: isVerified = true
  PrepareInputs() [server only]
  for each system in _systems:
    system.PrepareInput(...)
    system.SaveState(localTick) [non-event-handlers]
  WriteFrameOnServer() [server only]
  for each system: system.OnPrepareSimulationInputs(tick, delta)
  for each system: system.RunSimulateTick(tick, delta)
  DoPhysicsPass()
  for each system: system.RunLateSimulateTick(delta)
  for each system: system.PostSimulate()
  isSimulating = false
  localTick += 1
```

**Every PredictedIdentity's `Simulate` runs on every peer, every tick.** Verified:

```csharp
// PredictedIdentityStatefull.cs — SimulateTick
internal override void SimulateTick(ulong tick, float delta)
{
    Simulate(ref fullPredictedState.state, delta);
}

// PredictedIdentityWithInput.cs — SimulateTick
internal override void SimulateTick(ulong tick, float delta)
{
    PreSimulate(_currentInput, ref fullPredictedState.state, delta);
}
```

No `IsOwner()`, no `cachedIsServer` branch. Unconditional. Only the *input source* differs (owner uses local history, non-owner uses stored/extrapolated).

## Replay — when a server delta arrives

`PredictionManager.OnPostTick` processes server frames on clients:

```
OnPostTick (only if _deltas.Count > 0 and not server):
  isSimulating = true
  isReplaying = true
  while _deltas.Count > 0:
    isVerified = true
    RollbackToFrame(packer, tick)
    SimulateFrame(verifiedTick, saveState: true)
    isVerified = false
  SimulateFrame(_lastVerifiedTick + 1, saveState: true)
  ReplayToLatestTick(_lastVerifiedTick + 2)
  isReplaying = false
  isSimulating = false
```

`ReplayToLatestTick` runs the same `SimulateFrame` loop — your `Simulate` fires again for each replayed tick.

**Critical:** `cachedIsServer` is NOT reassigned during the replay block. It keeps its `OnPreTick` value:
- Client during replay: `cachedIsServer = false`
- Server during replay: `cachedIsServer = true`

A `cachedIsServer` gate behaves identically during live and replay passes.

## System execution order

Systems are stored in `List<PredictedIdentity> _systems` and inserted in **sorted order** by `objectId.instanceId.value` first, then `componentId.value`. This ensures deterministic execution order across all peers that instantiate the same prefab.

When two predicted components on the same GameObject both write the same physics property (e.g., velocity), the one with the higher componentId runs later and wins.

## Flag truth table

| Pass | `isSimulating` | `isReplaying` | `isVerified` | `cachedIsServer` |
|---|---|---|---|---|
| Server live tick | true | false | **true** | true |
| Client live tick | true | false | false | false |
| Client replay (verified tick) | true | true | **true** | false |
| Client replay (forward ticks) | true | true | false | false |
| Server replay (verified) | true | true | true | true |
| Server replay (forward) | true | true | false | true |
| Outside tick (UpdateView, RPC, button) | **false** | false | false | last value |

## Key properties

- `isVerifiedAndReplaying` = `isVerified && isReplaying` (compound property, not a separate flag)
- `isSimulating` docstring: *"If this is false nothing should act on the state of the game and expect it to be correct."*
- `isInPhysicsPass` = true only during `DoPhysicsPass()` — useful for physics-gated logic

## When to gate with cachedIsServer inside Simulate

Use when computation has these properties:
- Calls a non-deterministic native API (`NavMesh.CalculatePath`, platform-varying raycasts)
- Result is reconciled state clients will integrate forward
- Expensive AND changes infrequently

Do NOT gate when:
- Pure float math on reconciled state (deterministic, cheap)
- Result feeds immediate physics integration (clients need it for responsiveness)
- Decision input is itself reconciled state (same decision everywhere)

**Trade-off**: server-only values lag clients by ~RTT/2 ticks. Acceptable when smoothing covers the gap. Unacceptable for instant-feel reactions (knockback, impulses, ability triggers).

## Physics pass

`DoPhysicsPass()` runs `scene.GetPhysicsScene2D().Simulate(delta)` (and/or 3D equivalent) after `SimulateTick` and before `LateSimulateTick`. This happens both during live ticks and replay ticks. `Physics2D.SyncTransforms()` runs after every rollback.

# Prediction Tests

Multi-process end-to-end tests for the prediction pipeline, modeled after PurrNet's `PlayModeTests`. Each process loads `Bootstrap.unity`, connects, then runs the scenario sequence in lockstep (server drives, clients ack). Results are written as JSON via `-results`.

## Scenarios

| Scenario | What it guards |
|---|---|
| `PredictionBootstrap` | Connection + PredictionManager spawn/tick on every peer |
| `ImmediatePredictionRpcRegressionScenario` | Sustained bidirectional immediate RPC traffic matching PurrDiction's input/frame signatures, plus PurrLeague-shaped predicted transform/rigidbody/input/state identities and real `PredictedPlayerSpawner` state under the inherited desync policy; varies nested payload lengths across batch and MTU boundaries and validates every fixed field and payload byte after compression/fragmentation |
| `BounceScenario` | Verified-gated physics events fire exactly once per physical event: a predicted rigidbody bounces and every peer's `isVerified`-gated, tick-deduped collision counter must equal the server's (repro for the multi-fire report) |
| `DeterministicAlignmentScenario` | Deterministic identities stay tick-aligned with synced state across the join seam; timed deterministic spawns produce identical instance ids everywhere (PurrNet v1.20.0-beta.160 regression class) |
| `PredictedPawnScenario` | Input round-trip, per-player owned identities, input-driven hierarchy spawns converge |
| `ReconnectScenario` | Disconnect/reconnect mid-simulation: rejoined client re-syncs and stays converged through new deterministic spawns |
| `ProjectileChainScenario` | Predicted projectile bursts create predicted muzzle and hit effects under tiny prefab pools; list-backed projectile/module state stresses rollback reuse |
| `StaticModuleReuseScenario` | Tick-pooled predicted identities rerun static module setup on reuse and reset list-backed module state |
| `DynamicModuleShapeScenario` | Reused predicted identities start with no stale dynamic modules, then add/remove different dynamic module shapes |
| `ProjectileChainReconnectScenario` | A client reconnects during an active projectile/VFX burst, stressing full-sync while dynamic modules and pooled effects churn |
| `ServerRelayScenario` | ServerRelay bodies remain kinematic on clients and only execute verified ticks |
| `SoftCorrectionScenario` | 3D soft-corrected bodies tolerate local divergence and converge without replay simulation |
| `SoftCorrection2DScenario` | 2D soft-corrected bodies follow the same convergence and replay guarantees |
| `OwnedRelayScenario` | PredictedIfOwned resolves to live prediction for the owner and server relay for non-owners |
| `TrailIntegrityScenario` | Predicted projectile trails retain their list-backed state through rollback and pooled reuse |
| `DesyncCorrectionScenario` | An intentionally corrupted client state receives an authoritative correction and converges |
| `PieceLifecycleScenario` | Predicted pieces preserve lifecycle, parent links, and identity agreement |
| `PieceReconnectScenario` | A reconnecting client restores the same piece hierarchy and state |
| `MixedNetworkIdentityScenario` | NetworkIdentity components inside a predicted prefab need no spawner component: the server spawns them with one contiguous id block when the predicted instance is created, clients spawn them from the verified topology with the same ids, a SyncVar and a buffered ObserversRpc reach every client, ownership follows the predicted owner, `HideFrom`/`ShowTo` despawn and respawn them on the hidden client only, and deleting the predicted instance despawns them everywhere. Runs alone via `-mixedNetworkIdentityScenarioOnly` |
| `TickAgreementScenario` | Input-driven deterministic shots agree on their ticks and resulting state across peers |
| `SoftCorrectionPoolReuseScenario` | A scoped soft-correction object preserves its policy through replay pooling, while a completed pooled lifetime cannot leak pose-correction accumulators into the next object |
| `GenericSoftCorrectionScenario` | An opted-in generic state consumes verified deltas and converges without rollback simulation |
| `ReplayPolicyTransitionScenario` | Entering SoftCorrection during reconcile freezes the body before the replay physics pass |
| `TriggerZoneListScenario` | User-style trigger handlers mutating a `DisposableList` inside identity state through `currentState` stay consistent: a ball rests inside a predicted trigger zone while per-player owned walkers oscillate across the boundary (mispredicted crossings churn the list through rollback), then park inside; every peer must keep the ball tracked on every frame and land on identical final membership (repro for the smoke-zone empty-list/`Not enough bits` report) |
| `DeterministicGauntletScenario` | Input-driven deterministic logic and `PredictedRandom` survive latency/jitter/loss byte-exactly: a scene-authored `DeterministicIdentity<INPUT,STATE>` accumulates an RNG stream and server-generated inputs for 200 ticks; every peer must land on the identical steps/seed/accumulator/input-sum. Canary for input-redundancy-window overruns and deterministic timeline shifts |
| `CliffRecoveryScenario` | Determinism converges after an outage longer than the input window: each client forces 100% simulated packet loss for 2.5s (past the 32-tick ack cliff), requires the distress full-frame heal to arrive, then digest-compares the world including deterministic state. Enforces the "determinism always converges" contract (needs `DEBUG`/`SIMULATE_NETWORK`; digest-only otherwise) |
| `ViewPhaseScenario` | Every identity presents the same prediction tick, whenever it spawned: three server-controlled probes whose state is their own simulated tick are spawned at staggered wall-clock times; after each settles, the per-frame spread between their interpolated stamps and their deviation from `PredictionManager.viewTick` must stay within 0.02 ticks. Runs alone via `-lagCompensationScenariosOnly` |
| `LagCompensationScenario` | Lag-compensated hitscan lands where the shooter saw the target: a `ColliderRollback` target slides on a tick-linear path, every player fires along it through `lagCompensation.Raycast(lagCompensationTick, ...)`, and each hit point is converted back into the tick the collider history was sampled at. The server's authoritative shots and each controller's own prediction must land within 0.2 ticks of the presented pose, no shot may miss, and shot/hit counts converge. Runs alone via `-lagCompensationScenariosOnly` |
| `InputBandwidthScenario` | Not part of the suite; runs alone via `-inputBandwidthBenchmark` (see `run-input-bench.sh` in the repo root). Measures per-peer transport KB/s, upload window size/bytes, and frame section shares with worst-case (`-ibNoise full`), smooth, or constant inputs, optionally with `HalfVector3` payloads (`-ibHalf`) |
| `HistoryStressScenario` | Opt in with `-includeHistoryStressScenario`; exercises copied and disposed array/list state across 256 identities and compares final contents |

The normal suite contains 28 scenarios including bootstrap; history stress brings it to 29. The three benchmark selectors run separately. All scenarios fail on unexpected Unity `Error`, `Assert`, or `Exception` logs during setup or the active scenario. Setup failures abort the suite with explicit failed results because prefab registration is shared. The serialized scene defaults to 20 Hz and 100–150ms simulated latency; CI explicitly uses 40–80ms for its default profile. Set `-tickRate`, `-latencyMin`, and `-latencyMax` explicitly for reproducible runs; `-latencyMax 0` disables latency simulation.

Convergence is asserted by exchanging a world digest (deterministic counter delta vs `time.tick`, hierarchy instance list, `nextInstanceId`, pawn states) — clients report theirs, the server fails on any mismatch.

Barriers and digest messages are keyed by scenario index and channel. A completed barrier only releases that exact barrier, and timeout results propagate as failures. Ending a scenario cancels its outstanding work and rejects late synchronization messages while preserving valid early messages for the next scenario. Reconnects stay within the same scenario. Use fresh processes for each profile: the suite intentionally shares a world, prefab registry, and some static fixture state within a run.

## Running in the editor

Open `Bootstrap.unity` **in the main editor and in every clone**, then enter play mode in the main editor first and in the clones within the connection timeout (30s). The main editor runs as Host (configurable on the `Bootstrap` object); ParrelSync/MPPM clones auto-detect and join as clients. Default expected connections: 2 (host + one clone) — raise `Editor Expected Connections` when using more clones.

## Running standalone

Build `StandaloneLinux64`/`StandaloneWindows64` with this scene first, then:

```
PurrDictionTests -batchmode -nographics -role host -count 3 -results host.json -logFile host.log
PurrDictionTests -batchmode -nographics -role client -count 3 -results client-1.json -logFile client-1.log
PurrDictionTests -batchmode -nographics -role client -count 3 -results client-2.json -logFile client-2.log
```

Optional args: `-port`, `-serverHost`, `-connectTimeout`. Exit code is non-zero if any scenario fails. CI runs this via `.github/workflows/prediction-tests.yml` (server and host matrix, IL2CPP).

On Windows, `run-prediction-scenarios.ps1` launches and cleans up one complete profile:

```powershell
.\run-prediction-scenarios.ps1 -PlayerPath .\Builds\PurrDictionTests.exe -Mode host -TotalPlayers 4 -TickRate 60 -LatencyMin 40 -LatencyMax 80
```

`TotalPlayers` includes the host; dedicated mode launches that many external clients. The runner checks every process exit, every scenario result, and matching scenario names/order across peers. Pass `-ExpectedScenarioNames @(...)` to also enforce an explicit inventory; `-ExtraArguments @(...)` selects focused profiles. Each invocation writes separate logs, results, command arguments, and managed assembly hashes in a unique directory. `-DryRun` writes the launch plan without starting processes.

Pass `-immediateRpcRegressionScenarioOnly` to run only bootstrap plus the immediate-RPC
regression. Its default 180-prediction-tick burst can be changed with
`-immediateRpcRegressionFrames`; the burst is paced by prediction ticks rather than uncapped
headless render frames. Add `-immediateRpcRegressionWithoutPlayer` to disable the scene's
`PredictedPlayerSpawner` and assert that the client receives the correct built-in
`PredictedPlayers` membership while no owned gameplay identity exists.
Pass `-webTransport` to replace the scene's UDP transport with WebTransport; CI combines these
flags in a focused profile so the WebSocket-backed path is covered as well as UDP.
`-tickRate` overrides the scene's default 20 Hz rate and `-mtuFragment` selects the global
unreliable-fragment behavior. CI's focused UDP and WebTransport profiles use 60 Hz plus Fragment
to match the PurrLeague project settings that exposed this regression. They also pass
`-desyncPolicy Report`, which enables the new inherited deterministic-state reports while the
immediate prediction RPC traffic is active. The policy accepts `Ignore`, `Report`, `Resync`, or
`Correct`.

Keep `-immediateRpcRegressionWithoutPlayer` and the RPC workload's non-Ignore `-desyncPolicy` settings in the focused RPC profile. Its setup changes the shared player spawner, so combining those workload settings with the full suite would invalidate earlier pawn scenarios.

Policy regression scenarios are included in the normal suite. Pass `-policyRegressionScenariosOnly`
to run just the bootstrap and the three focused policy scenarios.

Pass `-physicsEventScenariosOnly` to run exactly bootstrap and the scene's serialized
`BounceScenario`. Its existing `BounceRig` reference is required. This focused selection
checks authoritative collision tick delivery and duplicate callbacks without running the
unrelated movement and input-timing scenarios. Use the Editor physics-event regressions
for 2D events and synthetic skipped-frame coverage.

Pass `-softCorrectionScenariosOnly` to run bootstrap and the serialized 3D/2D soft-correction
fixtures. They inject a measured 0.75m client-only position error plus a velocity kick, require
at least 0.3m of post-physics divergence, then require recovery within 0.15m for two seconds
without replay simulation. This keeps the disturbance independent of how far a velocity-only
kick travels before correction at different tick rates. Results include injection, peak, and
final distances.

The focused RPC scenario also checks the deterministic player-spawner map, which a correct
replicated roster or hierarchy alone cannot validate. `PredictedPlayers` guarantees its join/leave
input history so skipped frames still replay those callbacks at the original ticks. Optional
`-tracePlayerSpawner` diagnostics record roster events, spawner maps, and nearby history entries
during the initial join window.

Pool-reuse fixtures use 0.9-second lifetimes and 0.15-second gaps at every tick rate. A passing
client must observe a live pooled reuse after an earlier lifetime actually received a verified
correction; a positive reuse counter alone does not establish that the stale-correction check ran.

The active Editor `ReplayDriftChurnTests` also replays three spawns at drifting positions for
five rounds before pool retirement. It requires exactly three prefab clones and checks that
each logical identity retains its complete tree while its new spawn pose resets prediction state.

Authoritative-state regressions check that generated equality detects tiny corrections without
serializing, and that full and delta recipients agree on the exact baseline for subsequent deltas.
Sender omission and verified-store anchoring share that one comparison; received authority
replaces speculative history. Protocol-confirmed unchanged baselines remain sparse.

Verified client callback failures reject the incomplete frame and retain the previous ACK.
Recovery tests inject failures in input preparation, simulation, physics hooks, late simulation,
post-simulation and Unity-state capture, then require a successful checkpoint before delta replay
resumes. They also check that callbacks from partially completed gap ticks are not repeated. After
three consecutive rejected checkpoints the client falls back to logging callback exceptions and
continuing, as local simulation always has, until a frame applies cleanly; a deterministic
client-side fault therefore cannot leave a client unverified indefinitely.

The desync fixture keeps its deliberate fault armed from a scheduled tick until the probe's own
native desync notification arrives. Scenario cleanup stops injection on failure too. The test then
requires native correction and digest agreement; it never repairs the corrupted state itself.

The generic soft-correction fixture compares its live value with the last native target plus
the actual live simulation increments since that target. It requires a verified callback after
the 100-point fault, then checks the original 30-point residual bound. Raw distance to an older
snapshot also includes legitimate forward progress and is reported separately.

Shot-tick failures include bounded input-request/acceptance traces. Trail failures include
previous/current logical IDs, owners, physical instances, projectile ages, and verified ticks.
These diagnostics distinguish possible rejected input attempts, identity reuse, and corrections
without changing the existing failure thresholds.

`PhysicsEventGapDeliveryTests` separately tests verified collision delivery when an event-bearing
frame is skipped. It seeds an authoritative event and uses the real frame writer, receiver, and
reconciler with physics simulation disabled, so missing event history cannot be attributed to
cross-process PhysX drift.

Delta frames include ordered historical 3D/2D event batches from the acknowledged baseline.
Gap replay installs each batch at its original tick; overlapping deliveries do not fire again,
and historical visibility uses the event tick. Event capture continues while reliable delivery
is waiting for an ACK. Manual collision detection cannot append speculative events during
verified replay. The wire layout changed: rebuild both server and clients with the same SDK.

The ACK history window preserves 1.6 seconds (at least 32 ticks: 32 at 20 Hz, 96 at 60 Hz).
It is separate from the existing 32-tick upload/lead limits. The same window bounds how far a
client catches up from its verified tick in one transition: the server writes a delta only while
every tick after the baseline is retained, and the client replays exactly that far, so a valid
delta is never discarded and a checkpoint whose own delivery ran long can still be followed. A
larger gap requests a full snapshot. A reliable full left unacknowledged for longer than the
window can no longer anchor any delta; the server releases it and sends a fresh checkpoint rather
than suppressing that client. When a receiver's input roster is unchanged from the previous tick,
the transcript carries one repeat bit per entry in a per-tick mask and omits the unchanged
payloads, so delta size does not scale with ACK lag. The mask is followed by zero bits up to the
next byte boundary before the entries: frames are LZ4-compressed per RPC, and keeping consecutive
tick blocks byte-aligned is what lets the compressor match them. A flag bit inside each entry
halves the raw size but costs about a quarter more on the wire.

A confirmed missing acknowledged identity baseline or explicitly repeated input payload now
requests a full snapshot as soon as
the client detects it. Requests retry once per second even without incoming frames, and stop
after a covering full snapshot can be acknowledged. The server releases an unusable delta's
pending delivery immediately, coalesces requests already covered by a pending full snapshot,
and ignores delayed duplicate requests once that full snapshot is acknowledged. This path
has its own throttle so an earlier optional deterministic-desync heal cannot delay the first
baseline repair. The existing delta timeout remains a fallback for absent feedback.

Explicit delta records require the identity's own baseline; the sender uses a full identity
record when its baseline is unavailable. Sparse predecessor anchors remain valid. This check
does not reject new modules whose state legitimately starts from a default baseline. Generic
decoder errors and incompatible static module rosters do not initiate this missing-baseline
request path.

A full snapshot remains a timeline reset: only its current-tick events are delivered, since
older effects are already represented in its state. Durable gameplay totals belong in predicted
state; external notification counters cannot assume delivery across that reset. Historical event
endpoints must also exist when their original tick is replayed. Optional `-physicsEventTrace`
logs at most 512 capture/dispatch/reset records per manager, excluding Stay events. The focused
validation audit compares all strong server impacts by original tick, in addition to the existing
Bounce scenario's capped digest, so later bounces cannot conceal an earlier missing callback.

## Server load and visibility benchmark

Pass `-serverLoadBenchmark` to run only the bootstrap plus `ServerLoadBenchmarkScenario`.
The server spawns `-benchObjects` movers (default 200), settles, then samples acknowledgement
lag and frame-write profiler markers for `-benchSeconds` (default 20). Visibility markers cover
per-player preparation, event commits, and hierarchy/3D/2D projection. Direct player runs support:

- `-benchVisibilityMode none|static|churn|acquire-churn` (`none` is the default-visible baseline).
- `-benchVisibilityPercent` (default 25).
- `-benchVisibilityChurnPercent` (default 10) and `-benchVisibilityChurnTicks` (default 30).
- `-benchDeleteChurn <perSecond>` deletes and respawns that many movers per second during the
  timed window (steady total population, real `hierarchy.Delete` plus prefab respawn), exercising
  the per-frame delete tombstone section. Requires the default `-benchVisibilityMode none`.

`static` issues one-time `HideFrom` calls for the hidden complement. `churn` keeps the configured
visible fraction and issues only `HideFrom`/`ShowTo` changes at epoch boundaries. `acquire-churn`
hides the benchmark cohort once, holds `AcquireVisibility` handles for the visible window, and swaps
only the entering/leaving leases. Churn at the 0%/100% edges is capped to an achievable value and
reported as capped churn. An exact per-client RPC barrier prevents timing from starting with initial
state still in flight, including when a client expects zero visible movers. After sampling and timed
transport counters stop, every client also validates the exact sorted visible-root signature before
the scenario can pass.

Use `Tools/PurrDiction/Analysis/Run Server Load Latency Sweep` for the unchanged latency baseline.
`Tools/PurrDiction/Analysis/Run Server Load Visibility Sweep` runs a four-case
`none|static|churn|acquire-churn` preset at 0 ms below
`Builds/ServerLoadBenchmark/VisibilityPreset`. Use
`-executeMethod PurrNet.Prediction.Benchmarks.Editor.ServerLoadBenchmarkRunner.RunFromCommandLine`
to build once and run a matrix. With no new arguments, its workload is unchanged: 3 clients, 200
objects, visibility `none`, and latencies `0,50,100,200`. Opt-in CSV dimensions are
`-slbClientCounts`, `-slbObjectCounts`, `-slbVisibilityModes`, and `-slbLatencies`; legacy scalar
`-slbClients` and `-slbObjects` remain supported. Visibility settings use
`-slbVisibilityPercent`, `-slbVisibilityChurnPercent`, and `-slbVisibilityChurnTicks`.
`-slbVisibilityModes` also accepts `deletechurn`, which runs the delete-churn workload (visibility
left fully open) at the `-slbDeleteChurn` rate (default 10 per second).

The Cartesian product is capped at 32 runs by default so an accidental matrix does not monopolize CI;
raise that deliberate guard with `-slbMaxRuns`. For example:

```text
Unity.exe -batchmode -nographics -projectPath <project> -executeMethod PurrNet.Prediction.Benchmarks.Editor.ServerLoadBenchmarkRunner.RunFromCommandLine -slbClientCounts "1,4" -slbObjectCounts "200,1000" -slbVisibilityModes "none,static,churn,acquire-churn" -slbLatencies "0,100" -slbVisibilityPercent 25 -slbVisibilityChurnPercent 10 -slbVisibilityChurnTicks 30 -slbMaxRuns 32 -logFile server-load-runner.log
```

Results land in `Builds/ServerLoadBenchmark/server-load-sweep.{md,json}` with one artifact directory
per case. Reports include acknowledgement lag, visibility mutations/acquisitions/releases/active
handles/epochs, final-signature validation counts, every sampled marker (including
`CommitVisibilityChanges`), timed-window host bytes, and separately labeled whole-scenario host
bytes. Delivery-cadence columns cover the timed window: reliable frames (per-client frames that
took the reliable recovery path), full frames (non-delta frames), the per-case delete-churn rate
with delete/respawn counts, and the client-averaged `framesPerSecond` (server-frame apply passes
per second on each pure client). Other runner controls include `-slbSeconds`, `-slbInputEvery`,
`-slbPacketLoss`, `-slbSkipBuild`, and `-slbPlayer`.

## FULL prediction physics benchmark

Pass `-fullPredictionPhysicsBenchmark` to run only bootstrap and a dedicated-server physics
workload. Launch one `-role server -count N` process and N separate `-role client -count N`
processes with identical workload arguments and individual `-results` / `-fpMetrics` paths.
The normal transport, latency, loss, port, and `-tickRate` arguments still apply.

- `-fpBodiesPerPlayer 8`: owned rigidbodies per player.
- `-fpSharedBodies 16`: additional passive shared rigidbodies.
- `-fpTotalBodies N`: optional fixed total population; overrides the computed total and distributes
  `N - sharedBodies` owned bodies across all players. This must leave at least one body per player.
- `-fpSeconds 10`: measured simulation duration.
- `-fpSettleSeconds 3`: settling duration before the shared future measurement window (with a
  further three-second scheduling lead).
- `-fpReconcileMs 0`: client reconciliation cadence; zero preserves normal reconciliation.
  Active clients normally reconcile once in Update after all catch-up ticks and receive polls.
  Positive values defer client batches for comparison while retaining FULL prediction.
  A tiny positive interval such as `0.001` diagnoses a one-batch-per-render-frame limit:
  Unity's unscaled frame time is constant within that frame. This historical probe is now
  redundant for active clients, whose normal scheduling already coalesces corrections per frame.
- `-fpEventMask 0`: production predicted physics event mask (0..127); defaults to no replicated
  contact events. Native Enter/Exit contact counters still validate the colliding workload; the
  body deliberately declares no Stay message, since that alone forces per-step contact reporting.
- `-fpPolicy FullPrediction`: configured prediction policy for every body component (`FullPrediction`,
  `ServerRelay`, `SoftCorrection`, `PredictedIfOwned`, `PredictedIfOwnedWithSoftFallback`). Per-client
  resolution is the production behaviour; reports record the resolved distribution. Relay clients
  keep bodies kinematic and are exempt from the contact requirement. The runner exposes it as `-Policy`.
- `-fpMetrics path.json`: per-process detailed benchmark output, separate from scenario results.

Every body uses the production `PredictedRigidbody` and `PredictedTransform`, explicitly configured
for `FullPrediction` with `Purrfect` state accuracy. Only Unity 3D physics runs. Owned bodies generate
integer inputs from simulation tick, owner ID and object ID, send them through normal input history,
perform one grounding raycast, and apply steering forces inside a floor-and-wall arena. Shared
props are passive and collide with the controlled bodies. Simulation does not use wall-clock
randomness, logging or managed allocations in its workload callbacks.

A readiness barrier precedes a shared `[startTick, endTick)` measurement window. Outputs include
actual and scheduled ticks, body counts, native contact callback/contact-point counts, contacts
between bodies, grounding queries, categorized prediction/physics/frame telemetry, sampled frame
counts/bytes and maximum observed server acknowledgement lag. The
scenario fails if the window is missed, a body is not dynamic FULL prediction, or the sampled
workload has no contacts between bodies or no gameplay grounding queries.

Telemetry stops before validation. Peers record which authoritative ticks actually applied every
benchmark body (excluding gap replay), then select the newest tick common to all peers from a
one-second window. A later verified tick alone does not prove that an earlier UDP frame arrived.
Reports include the requested cutoff, whether it applied locally, each peer's candidates, and the
selected tick. Every peer reads its actual verified pose/motion histories at that selected tick;
the server checks all client results. `verifiedStateMatch` checks state delivery at the same tick,
including body IDs, positions, rotations, velocities, sleeping, gravity and kinematic flags. It does
not claim that speculative physics trajectories are deterministic or equal across peers. Reflection
used to inspect the production histories is confined to this validation stage.
The benchmark also fails if an active client performs more than one correction batch within a
complete measured render frame, exercising the catch-up scheduling invariant under real load.

Run 1, 2 and 4 clients with the default body formula to measure combined player/object growth,
then repeat with the same `-fpTotalBodies` to separate connection growth from physics population.
Compare cadence runs using the same build, workload and network conditions; timing is comparative
data, not a machine-independent pass/fail threshold.

Build a Development Mono player with the editor command-line entry point
`PurrNet.Prediction.Benchmarks.Editor.FullPredictionBenchmarkBuild.BuildWindowsDev`, passing
`-fpPlayerPath <absolute-output.exe>`. The root `run-full-prediction-bench.ps1` launches sequential
dedicated-server cases with hidden clients, collects results, and records hardware, build metadata
and binary/managed-assembly hashes. For example:

```powershell
./run-full-prediction-bench.ps1 -ClientCounts 1,2,4 -LatencyMs 0,50 -ReconcileMs 0 -Repeats 2
./run-full-prediction-bench.ps1 -ClientCounts 4 -LatencyMs 50 -ReconcileMs 0,33.333 -Repeats 3
./run-full-prediction-bench.ps1 -Scenario Regression -ClientCounts 4 -LatencyMs 50 -ReconcileMs 0,33.333 -RunTimeoutSeconds 240
./run-full-prediction-bench.ps1 -Scenario Trail -ClientCounts 4 -LatencyMs 50 -ReconcileMs 0 -RunTimeoutSeconds 240
python analyze-full-prediction-bench.py <physics-run-directory> --output <summary-directory>
```

With latency configured at both endpoints, the pinned transport's `50` setting adds nominally
50 ms one way (100 ms RTT), plus scheduling. All processes share the same machine; avoid other
heavy work while measuring. Telemetry uses elapsed Stopwatch spans, which include worker waits
and process descheduling. It is not a measurement of processor utilization or rendered FPS.
The analyzer averages clients within each case before comparing independent repeats. Input
trajectories depend on absolute simulation tick and owner assignment, so repeat runs and fixed
population controls do not guarantee identical contact configurations.

`PredictionPerformanceTelemetry.Begin(world)` and `End()` can also bracket a representative
workload in another project. Collection is disabled until `Begin`. The experimental global
`reconcileIntervalSeconds` defaults to zero; a positive value delays corrections and verified
callbacks. `End()` stops collection but leaves that cadence active for subsequent validation.

`-fpReconcileMs` also applies outside the benchmark. Use `-fullPredictionCadenceRegressionOnly`
to select bootstrap, bounce events, deterministic alignment with the timed hierarchy spawner,
predicted pawns, first-predicted/verified shot-tick agreement, the deterministic gauntlet and projectile chains from the existing test scene,
then compare default cadence and a positive interval. The alignment setup initializes the timed
spawner used by the later shared-tick digest gates. This
keeps event/topology correctness checks separate from the physics timing workload.

For this exact profile flag, `run-prediction-scenarios.ps1` automatically enables
`-physicsEventTrace` and checks continuity with `prediction-scenario-continuity.ps1`.
Every peer must log all seven scenarios' start/finish boundaries in order, pass each one,
and retain complete recovery traces below the shared 512-record cap. Full checkpoints
are allowed only with a zero previous/baseline tick before Bounce starts, or after the
final ProjectileChain PASS; recovery during the suite fails even if its final digests pass.
Missing or malformed traces, recovery requests, and log errors also fail. `case.json`
records the per-peer diagnostics and aggregate continuity result, and `manifest.json`
records the validator hash. Intentional recovery profiles keep their existing checks.
Run `./test-prediction-scenario-continuity.ps1` for offline parser regressions; no Unity
player is launched. The helper can also be dot-sourced to check preserved logs with
`Test-FullPredictionContinuity -LogPath <peer.log> -Role server|host|client`.

## Visibility microbenchmarks

Use `Tools/PurrDiction/Analysis/Run Visibility Benchmarks` to measure the per-player visibility
hot paths without adding machine-dependent timing assertions to the test suite. The runner covers
`HideFrom`, `ShowTo`, `AcquireVisibility`, and handle-disposal event submission; idempotent and sparse
timeline mutations; initial and stable ACK pruning; hierarchy projection (including deletes and a
16-player batch); baseline-root membership; addressed record encoding/decoding; and 3D/2D
physics-event projection. It reports median/min/max time, steady-state allocation, normalized time
per source record, output counts, and encoded bit counts to
`Temp/PurrDictionVisibilityBenchmarks/prediction-visibility-benchmarks.{json,md}`.

For batch-mode comparisons, run:

```text
Unity.exe -batchmode -nographics -projectPath <project> -executeMethod PurrNet.Prediction.Benchmarks.Editor.PredictionVisibilityBenchmarkRunner.RunFromCommandLine -purrdictionVisibilityBenchmarkOutput Temp/PurrDictionVisibilityBenchmarks -logFile Temp/PurrDictionVisibilityBenchmarks/benchmark.log
```

Treat results as comparative data from the same machine and build configuration; these benchmarks
are intentionally opt-in and non-gating.

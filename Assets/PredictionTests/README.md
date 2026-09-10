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
| `SoftCorrectionPoolReuseScenario` | A scoped soft-correction object preserves its policy through replay pooling, while a completed pooled lifetime cannot leak pose-correction accumulators into the next object |
| `GenericSoftCorrectionScenario` | An opted-in generic state consumes verified deltas and converges without rollback simulation |
| `ReplayPolicyTransitionScenario` | Entering SoftCorrection during reconcile freezes the body before the replay physics pass |
| `TriggerZoneListScenario` | User-style trigger handlers mutating a `DisposableList` inside identity state through `currentState` stay consistent: a ball rests inside a predicted trigger zone while per-player owned walkers oscillate across the boundary (mispredicted crossings churn the list through rollback), then park inside; every peer must keep the ball tracked on every frame and land on identical final membership (repro for the smoke-zone empty-list/`Not enough bits` report) |
| `DeterministicGauntletScenario` | Input-driven deterministic logic and `PredictedRandom` survive latency/jitter/loss byte-exactly: a scene-authored `DeterministicIdentity<INPUT,STATE>` accumulates an RNG stream and server-generated inputs for 200 ticks; every peer must land on the identical steps/seed/accumulator/input-sum. Canary for input-redundancy-window overruns and deterministic timeline shifts |
| `CliffRecoveryScenario` | Determinism converges after an outage longer than the input window: each client forces 100% simulated packet loss for 2.5s (past the 32-tick ack cliff), requires the distress full-frame heal to arrive, then digest-compares the world including deterministic state. Enforces the "determinism always converges" contract (needs `DEBUG`/`SIMULATE_NETWORK`; digest-only otherwise) |
| `InputBandwidthScenario` | Not part of the suite; runs alone via `-inputBandwidthBenchmark` (see `run-input-bench.sh` in the repo root). Measures per-peer transport KB/s, upload window size/bytes, and frame section shares with worst-case (`-ibNoise full`), smooth, or constant inputs, optionally with `HalfVector3` payloads (`-ibHalf`) |

All scenarios fail on unexpected Unity `Error`, `Assert`, or `Exception` logs during the active scenario. They run with simulated latency (40–80ms by default, configurable on the `Bootstrap` object or via `-latencyMin`/`-latencyMax`; `-latencyMax 0` disables) so rollback depth resembles real conditions instead of a clean localhost.

Convergence is asserted by exchanging a world digest (deterministic counter delta vs `time.tick`, hierarchy instance list, `nextInstanceId`, pawn states) — clients report theirs, the server fails on any mismatch.

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

Policy regression scenarios are included in the normal suite. Pass `-policyRegressionScenariosOnly`
to run just the bootstrap and the three focused policy scenarios.

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
  contact events. Native contact counters still validate the colliding workload.
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
predicted pawns, the deterministic gauntlet and projectile chains from the existing test scene,
then compare default cadence and a positive interval. The alignment setup initializes the timed
spawner used by the later shared-tick digest gates. This
keeps event/topology correctness checks separate from the physics timing workload.

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

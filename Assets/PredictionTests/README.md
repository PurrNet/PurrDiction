# Prediction Tests

Multi-process end-to-end tests for the prediction pipeline, modeled after PurrNet's `PlayModeTests`. Each process loads `Bootstrap.unity`, connects, then runs the scenario sequence in lockstep (server drives, clients ack). Results are written as JSON via `-results`.

## Scenarios

| Scenario | What it guards |
|---|---|
| `PredictionBootstrap` | Connection + PredictionManager spawn/tick on every peer |
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
| `DeterministicGauntletScenario` | Input-driven deterministic logic and `PredictedRandom` survive latency/jitter/loss byte-exactly: a scene-authored `DeterministicIdentity<INPUT,STATE>` accumulates an RNG stream and server-generated inputs for 200 ticks; every peer must land on the identical steps/seed/accumulator/input-sum. Canary for input-redundancy-window overruns and deterministic timeline shifts |

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

Policy regression scenarios are included in the normal suite. Pass `-policyRegressionScenariosOnly`
to run just the bootstrap and the three focused policy scenarios.

## Interest management scenario

Pass `-interestScenariosOnly` to run the bootstrap plus `InterestManagementScenario`. This focused
scenario requires a pure server and exactly two clients. It creates one player-owned
`PredictedTransform` anchor per client, two distant deterministic roots, and reciprocal owned
keyed-input movers. It verifies culling, both sides of the LOD hysteresis band, dormant
simulation/view behavior, hierarchy retention, deterministic and input-driven absolute-state
convergence on reentry, and a per-recipient packed-frame byte reduction while culled.

```
PurrDictionTests -batchmode -nographics -role server -count 2 -interestScenariosOnly -results server.json -logFile server.log
PurrDictionTests -batchmode -nographics -role client -count 2 -interestScenariosOnly -results client-1.json -logFile client-1.log
PurrDictionTests -batchmode -nographics -role client -count 2 -interestScenariosOnly -results client-2.json -logFile client-2.log
```

## Server load benchmark

Pass `-serverLoadBenchmark` to run only the bootstrap plus `ServerLoadBenchmarkScenario`: a
`BenchDriver` spawns `-benchObjects` (default 200) input-driven `BenchMover` identities, then the
server samples the `WriteFrameOnServer` sub-markers (`WriteInputHistory`, `WriteStateDeltas`,
`WriteFullFrame`, `WriteEventHandles`, `SendFrame`) plus client ack lag for `-benchSeconds`
(default 20), records frame payload bytes per recipient through `TickBandwidthProfiler`, and
reports the normalized `bytesPerClientTick` in the scenario result message. Use
`Tools/PurrDiction/Analysis/Run Server Load Latency Sweep` (or
`-executeMethod PurrNet.Prediction.Benchmarks.Editor.ServerLoadBenchmarkRunner.RunFromCommandLine`)
to build the player and sweep several simulated latencies (`-slbLatencies "0,50,100,200"`,
`-slbClients`, `-slbObjects`, `-slbSeconds`, `-slbSkipBuild`, `-slbPlayer`); the per-latency
reports land in `Builds/ServerLoadBenchmark/server-load-sweep.md`. This is how ping-dependent
server frame-write cost is measured and compared.

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

PurrDiction is a client-side prediction (CSP) framework for Unity multiplayer games, built as an addon to PurrNet. It provides deterministic simulation with rollback, physics integration, delta compression, and IL codegen for fixed-point math.

- **Package**: `dev.purrnet.purrdiction` (UPM via git)
- **Namespace**: `PurrNet.Prediction`
- **Dependencies**: PurrNet (networking), Mono.Cecil (IL codegen)

## Build & Test

This is a Unity project — there is no CLI build command. Tests run via Unity Test Runner (Window > General > Test Runner). The test assembly is `PurrNet.Prediction.Tests` under `Assets/PurrDictionTests/`.

## CI/CD & Commits

Releases are automated via semantic-release on GitHub Actions (`.github/workflows/release.yml`). Pushing to `dev` creates beta releases; pushing to `release` creates stable releases. Commit messages must follow [Conventional Commits](https://www.conventionalcommits.org/) for the analyzer to work:

- `fix: description` — triggers patch bump
- `feat: description` — triggers minor bump
- Breaking changes in footer trigger major bump

## Architecture

### Prediction Lifecycle

The core loop runs per-tick in fixed timestep:

```
Simulate(tick, delta) → LateSimulate(delta) → SaveState(tick)
         ↓ (on misprediction)
Rollback(tick) → re-simulate forward
         ↓ (every frame)
UpdateView(delta) → LateUpdateView(delta)  [visual interpolation]
```

### Core Type Hierarchy

`PredictedIdentity` (abstract) is the base for all predicted entities. It attaches to GameObjects and manages a list of `PredictedModule` instances. Key variants:

- `PredictedIdentity<TState>` — generic with a state type implementing `IPredictedData<T>`
- `StatelessPredictedIdentity` — prediction without state tracking
- `DeterministicIdentity` / `DeterministicIdentityWithInput` — input-free deterministic behavior

### Module System

`PredictedModule` is an abstract base for pluggable behaviors. Modules register with an identity via `RegisterModule()` and implement lifecycle hooks: `Simulate`, `LateSimulate`, `Rollback`, `SaveState`, `WriteState`/`ReadState`, `UpdateView`. Modules have an internal/protected pattern where internal methods (e.g., `SimulateInternal`) delegate to protected virtuals/abstracts.

### State Interfaces

- `IPredictedData` — extends `IDisposable` + `IPackedAuto` (auto-serialization codegen)
- `IPredictedData<T>` — adds `IMath<T>` (Add, Scale, Lerp) for interpolation and rollback blending

### Serialization

Uses PurrNet's packer system: `Packer<T>`, `DeltaPacker<T>`, `NativePacker<T>`, `NativeDeltaPacker<T>`. States implement `IPackedAuto` for compile-time codegen of serialization. Delta compression is handled by `DeltaModule`.

### IL Codegen (Assets/PurrDiction/Codegen/)

Two processors run as Unity `ILPostProcessor` implementations via Mono.Cecil:

- **PredictionProcessor** — entry point; processes assemblies referencing `PurrNet.Prediction`
- **FPProcessor** — rewrites float/double arithmetic IL to use fixed-point (`FP`, `sfloat`) operations for determinism

### Deterministic Math

Two parallel math libraries for cross-platform determinism:
- **FixedPoint** (`Runtime/FixedPoint/`): `FP`, `FPVec2`, `FPVec3`, `FPVec4`, `FPQuat`
- **SoftFloat** (`Runtime/SoftFloat/`): `sfloat`, `SVec2`, `SVec3`, `SVec4`, `SQuat`

### Physics

`PredictedRigidbody` and `PredictedRigidbody2D` wrap Unity physics for rollback-compatible simulation. Guarded by conditional compilation: `UNITY_PHYSICS_3D`, `UNITY_PHYSICS_2D`.

## Code Conventions

- Private fields use `_camelCase` prefix
- Predicted types are prefixed with "Predicted" (e.g., `PredictedTransform`, `PredictedRigidbody`)
- Hot paths use `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
- IL-referenced members use `[UsedImplicitly]`
- Runtime assembly has unsafe code enabled
- Large types use partial classes (e.g., `PredictedIdentity` split across files)

## Assembly Definitions

| Assembly | Purpose |
|---|---|
| `PurrNet.Prediction` | Main runtime (unsafe enabled) |
| `PurrNet.Prediction.Editor` | Inspector/profiler UI |
| `PurrNet.Prediction.Prebuilt` | Ready-made CharacterController & Rigidbody modules |
| `Purrdiction.Codegen` | IL post-processors |
| `PurrNet.Prediction.Tests` | Test suite |

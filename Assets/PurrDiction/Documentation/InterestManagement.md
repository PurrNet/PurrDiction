# Interest management

PurrDiction interest management assigns each predicted root a per-player network LOD tier. The tier controls state-send frequency, the client-side prediction policy, and whether a remote root has a live client-side GameObject. Hierarchy records continue to replicate while a root is culled; interest management is a bandwidth and simulation optimization, not an authority or security boundary.

## Setup

Create a `NetworkLODProfile` and configure its distance bands, hysteresis, send intervals, and `cullBeyondLastTier`. Then create a `PredictionLODProfile`, assign the network profile, configure the prediction policy for each visible tier and the culled tier, and assign it to `PredictionManager.predictionLODProfile`.

Interest management is enabled only while both profiles are assigned. Access the active API through `PredictionManager.interest` after the manager has spawned.

```csharp
var interest = predictionManager.interest;
if (interest != null && interest.enabled)
{
    var profile = interest.profile;
}
```

`PredictionLODProfile` exposes:

- `networkProfile`: the `NetworkLODProfile` used for distance resolution and send intervals.
- `tierCount`: the number of configured prediction-policy tiers.
- `Configure(...)`: runtime configuration for programmatically created profiles.
- `GetSuggestedPolicy(tier)`: the configured `PredictionPolicyOverride` for a tier.
- `TryGetSuggestedPolicy(tier, out policy)`: resolves overrides other than `KeepConfigured` to a concrete `PredictionPolicy`.

If a network tier has no matching prediction tier, the last prediction tier is reused. `KeepConfigured` leaves the identity's configured policy unchanged.

## Querying tiers

On the server, query the effective tier for one player and root with `TryGetTier`:

```csharp
if (predictionManager.interest.TryGetTier(player, root, out var tier))
{
    bool culled = tier == NetworkLODProfile.CulledTier;
}
```

On a client, query its received tier with `TryGetLocalRelevance`. Tier zero is stored implicitly, so a `false` result with `tier == 0` is the normal full-detail default:

```csharp
predictionManager.interest.TryGetLocalRelevance(root, out var localTier);
bool locallyCulled = localTier == NetworkLODProfile.CulledTier;
```

The PurrDiction Profiler displays this current client tier beside each live state or input row. A dash means that the row has no live client interest context, including server samples, because a server row can be written for multiple receiver tiers.

## Events

Client tier changes are reported by `OnLocalRelevanceChanged`:

```csharp
predictionManager.interest.OnLocalRelevanceChanged += (root, previousTier, currentTier) =>
{
    bool becameRelevant = previousTier == NetworkLODProfile.CulledTier &&
                          currentTier != NetworkLODProfile.CulledTier;
};
```

The event includes visible-to-visible changes as well as transitions across the culled boundary. A culled root has no live client-side GameObject. When it becomes relevant again, PurrDiction rematerializes it from the retained hierarchy records and applies the re-entry absolute state before reporting the new tier.

Server-side events carry the affected `PlayerID`:

- `OnServerTierChanged(player, root, previousTier, currentTier)` fires for every effective-tier change.
- `OnServerBecameRelevant(player, root)` fires only when a root leaves `NetworkLODProfile.CulledTier`.
- `OnServerBecameIrrelevant(player, root)` fires only when a root enters `NetworkLODProfile.CulledTier`.

Subscribe on the server after `PredictionManager.interest` is initialized. These events describe replication relevance for one player; they do not transfer ownership or grant authority.

## Pins

Server code can override one player/root pair with `SetPin`:

```csharp
var interest = predictionManager.interest;
interest.SetPin(player, root, PredictionInterestPin.Relevant);
interest.SetPin(player, root, PredictionInterestPin.Culled);
interest.SetPin(player, root, PredictionInterestPin.Default);
```

- `Relevant` forces tier zero.
- `Culled` forces `NetworkLODProfile.CulledTier`.
- `Default` removes the pin and resumes normal resolution.

The method returns `true` when the stored pin changed. Ownership has final precedence, so an owned root remains tier zero even when it has a culled pin.

## Custom tier providers

Assign an `IPredictionInterestProvider` to `PredictionInterestModule.provider` to adjust the distance-resolved tier per player and root:

```csharp
public sealed class TeamInterestProvider : IPredictionInterestProvider
{
    public byte ResolveTier(
        PredictionManager manager,
        PlayerID player,
        PredictedObjectID root,
        byte computedTier)
    {
        return ShouldUseFullDetail(player, root)
            ? (byte)0
            : computedTier;
    }
}

predictionManager.interest.provider = new TeamInterestProvider();
```

Return a configured tier index or `NetworkLODProfile.CulledTier`. The provider runs after the prefab tier floor and before pins and ownership. Reassigning the provider affects subsequent tier refreshes.

## Custom scheduling

Assign an `ILODScheduler` to `PredictionInterestModule.scheduler` to control which ticks serialize state for a visible tier:

```csharp
public sealed class EvenTickScheduler : ILODScheduler
{
    public bool ShouldSendThisTick(
        ILODTarget target,
        NetworkLODProfile profile,
        PlayerID player,
        byte tier,
        uint tick)
    {
        return (tick & 1) == 0;
    }
}

predictionManager.interest.scheduler = new EvenTickScheduler();
```

When `scheduler` is `null`, PurrDiction uses `LODIntervalScheduler.instance`, which applies `NetworkLODProfile.GetSendIntervalTicks(tier)` with per-root staggering. The scheduler is consulted only for visible, non-forced state sends; culled roots do not serialize state, and required absolute re-entry state bypasses normal rate scheduling.

## Per-prefab tier floor

Each entry in the `PredictedPrefabs` asset has a `minimumInterestTier` field, shown as **Min Tier** in its inspector:

- `0` permits full detail and preserves existing behavior.
- A higher configured tier prevents distance resolution from selecting a more detailed tier.
- `255` (`NetworkLODProfile.CulledTier`) starts the prefab culled for remote players.

The value is a lower-detail floor, not a relevance guarantee. Resolution order is distance tier, prefab floor, custom provider, pin, then ownership. Out-of-range visible values are clamped to the last configured network tier. Auto-generation preserves the value on matching prefab entries.

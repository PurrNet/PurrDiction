using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Selects the prediction policy applied while a root occupies an interest tier.
    /// </summary>
    public enum PredictionPolicyOverride : byte
    {
        KeepConfigured,
        FullPrediction,
        ServerRelay,
        SoftCorrection,
        PredictedIfOwned
    }

    /// <summary>
    /// Configures prediction behavior for one visible network LOD tier.
    /// </summary>
    [Serializable]
    public struct PredictionLODTier
    {
        /// <summary>
        /// The policy override applied while a root occupies this tier.
        /// </summary>
        public PredictionPolicyOverride suggestedPolicy;
    }

    /// <summary>
    /// Maps network LOD tiers to client prediction policies.
    /// </summary>
    [CreateAssetMenu(menuName = "PurrDiction/Prediction LOD Profile", fileName = "New Prediction LOD Profile")]
    public sealed class PredictionLODProfile : ScriptableObject
    {
        [SerializeField] private NetworkLODProfile _networkProfile;
        [SerializeField] private PredictionLODTier[] _tiers = Array.Empty<PredictionLODTier>();
        [SerializeField] private PredictionPolicyOverride _culledPolicy = PredictionPolicyOverride.ServerRelay;

        /// <summary>
        /// Gets or sets the network LOD profile that resolves distance tiers and send intervals.
        /// </summary>
        public NetworkLODProfile networkProfile
        {
            get => _networkProfile;
            set => _networkProfile = value;
        }

        /// <summary>
        /// Assigns a network profile while leaving prediction tiers at their configured defaults.
        /// </summary>
        public void Configure(NetworkLODProfile networkProfile)
        {
            _networkProfile = networkProfile;
        }

        /// <summary>
        /// Gets the number of configured visible prediction tiers.
        /// </summary>
        public int tierCount => _tiers?.Length ?? 0;

        /// <summary>
        /// Configures the network profile, visible tier policies, and culled policy.
        /// </summary>
        public void Configure(
            NetworkLODProfile networkProfile,
            PredictionLODTier[] tiers,
            PredictionPolicyOverride culledPolicy = PredictionPolicyOverride.ServerRelay)
        {
            _networkProfile = networkProfile;
            _tiers = tiers;
            _culledPolicy = culledPolicy;
        }

        /// <summary>
        /// Gets the configured policy override for a tier.
        /// </summary>
        public PredictionPolicyOverride GetSuggestedPolicy(byte tier)
        {
            if (tier == NetworkLODProfile.CulledTier)
                return _culledPolicy;
            if (_tiers == null || _tiers.Length == 0)
                return PredictionPolicyOverride.KeepConfigured;
            return _tiers[Mathf.Min(tier, _tiers.Length - 1)].suggestedPolicy;
        }

        /// <summary>
        /// Resolves a tier to a concrete policy when it does not keep the configured policy.
        /// </summary>
        public bool TryGetSuggestedPolicy(byte tier, out PredictionPolicy policy)
        {
            switch (GetSuggestedPolicy(tier))
            {
                case PredictionPolicyOverride.FullPrediction:
                    policy = PredictionPolicy.FullPrediction;
                    return true;
                case PredictionPolicyOverride.ServerRelay:
                    policy = PredictionPolicy.ServerRelay;
                    return true;
                case PredictionPolicyOverride.SoftCorrection:
                    policy = PredictionPolicy.SoftCorrection;
                    return true;
                case PredictionPolicyOverride.PredictedIfOwned:
                    policy = PredictionPolicy.PredictedIfOwned;
                    return true;
                default:
                    policy = default;
                    return false;
            }
        }

    }
}

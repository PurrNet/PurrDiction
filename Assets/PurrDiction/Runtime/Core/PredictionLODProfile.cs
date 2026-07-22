using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    public enum PredictionPolicyOverride : byte
    {
        KeepConfigured,
        FullPrediction,
        ServerRelay,
        SoftCorrection,
        PredictedIfOwned
    }

    [Serializable]
    public struct PredictionLODTier
    {
        public PredictionPolicyOverride suggestedPolicy;
    }

    [CreateAssetMenu(menuName = "PurrDiction/Prediction LOD Profile", fileName = "New Prediction LOD Profile")]
    public sealed class PredictionLODProfile : ScriptableObject
    {
        [SerializeField] private NetworkLODProfile _networkProfile;
        [SerializeField] private PredictionLODTier[] _tiers = Array.Empty<PredictionLODTier>();
        [SerializeField] private PredictionPolicyOverride _culledPolicy = PredictionPolicyOverride.ServerRelay;

        public NetworkLODProfile networkProfile
        {
            get => _networkProfile;
            set => _networkProfile = value;
        }

        public void Configure(NetworkLODProfile networkProfile)
        {
            _networkProfile = networkProfile;
        }

        public int tierCount => _tiers?.Length ?? 0;

        public void Configure(
            NetworkLODProfile networkProfile,
            PredictionLODTier[] tiers,
            PredictionPolicyOverride culledPolicy = PredictionPolicyOverride.ServerRelay)
        {
            _networkProfile = networkProfile;
            _tiers = tiers;
            _culledPolicy = culledPolicy;
        }

        public PredictionPolicyOverride GetSuggestedPolicy(byte tier)
        {
            if (tier == NetworkLODProfile.CulledTier)
                return _culledPolicy;
            if (_tiers == null || _tiers.Length == 0)
                return PredictionPolicyOverride.KeepConfigured;
            return _tiers[Mathf.Min(tier, _tiers.Length - 1)].suggestedPolicy;
        }

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

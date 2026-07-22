using UnityEngine;

namespace PurrNet.Prediction
{
    [CreateAssetMenu(menuName = "PurrDiction/Prediction LOD Profile", fileName = "New Prediction LOD Profile")]
    public sealed class PredictionLODProfile : ScriptableObject
    {
        [SerializeField] private NetworkLODProfile _networkProfile;

        public NetworkLODProfile networkProfile
        {
            get => _networkProfile;
            set => _networkProfile = value;
        }

        public void Configure(NetworkLODProfile networkProfile)
        {
            _networkProfile = networkProfile;
        }
    }
}

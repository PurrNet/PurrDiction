using System;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Obsolete("NetworkIdentity components inside predicted prefabs are spawned automatically; remove this component.")]
    [AddComponentMenu("")]
    public sealed class PredictedIdentitySpawner : MonoBehaviour
    {
        [SerializeField] private NetworkIdentity[] _identitiesToSpawn;
    }
}

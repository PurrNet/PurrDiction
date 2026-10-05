using System;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct InstanceDetails : IPackedAuto, IEquatable<InstanceDetails>
    {
        public readonly PackedInt prefabId;
        public readonly PackedUInt pieceIndex;
        public readonly PredictedObjectID instanceId;
        public readonly Vector3 spawnPosition;
        public readonly Quaternion spawnRotation;
        public readonly PlayerID? owner;
        public readonly PredictedComponentID? parent;

        /// <summary>
        /// First PurrNet id of this piece's NetworkIdentity components, allocated by the server
        /// when the instance enters its topology. Null for pieces without network identities and
        /// for speculative client instances, which never spawn network identities.
        /// </summary>
        public readonly NetworkID? networkId;

        /// <summary>
        /// Id of the root piece of the spawn instance this piece belongs to.
        /// Piece ids are allocated as one contiguous block per spawn, so this is derived.
        /// </summary>
        public PredictedObjectID rootId => new PredictedObjectID(instanceId.instanceId.value - pieceIndex.value);

        public bool isRootRecord => pieceIndex.value == 0;

        public InstanceDetails(int prefabId, PredictedObjectID instanceId, Vector3 spawnPosition, Quaternion spawnRotation, PlayerID? owner)
            : this(prefabId, 0, instanceId, spawnPosition, spawnRotation, owner, null)
        {
        }

        public InstanceDetails(int prefabId, uint pieceIndex, PredictedObjectID instanceId, Vector3 spawnPosition, Quaternion spawnRotation, PlayerID? owner, PredictedComponentID? parent)
            : this(prefabId, pieceIndex, instanceId, spawnPosition, spawnRotation, owner, parent, null)
        {
        }

        public InstanceDetails(int prefabId, uint pieceIndex, PredictedObjectID instanceId, Vector3 spawnPosition, Quaternion spawnRotation, PlayerID? owner, PredictedComponentID? parent, NetworkID? networkId)
        {
            this.prefabId = prefabId;
            this.pieceIndex = pieceIndex;
            this.instanceId = instanceId;
            this.spawnPosition = spawnPosition;
            this.spawnRotation = spawnRotation;
            this.owner = owner;
            this.parent = parent;
            this.networkId = networkId;
        }

        public InstanceDetails WithNetworkId(NetworkID? id)
            => new InstanceDetails(prefabId.value, pieceIndex.value, instanceId, spawnPosition, spawnRotation, owner, parent, id);

        public bool Equals(InstanceDetails other)
        {
            // Topology baselines must preserve float bits and the owner's bot flag.
            return prefabId.value == other.prefabId.value && pieceIndex.value == other.pieceIndex.value &&
                   instanceId.Equals(other.instanceId) &&
                   SameOwner(owner, other.owner) && Nullable.Equals(parent, other.parent) &&
                   Nullable.Equals(networkId, other.networkId) &&
                   Bits(spawnPosition.x) == Bits(other.spawnPosition.x) &&
                   Bits(spawnPosition.y) == Bits(other.spawnPosition.y) &&
                   Bits(spawnPosition.z) == Bits(other.spawnPosition.z) &&
                   Bits(spawnRotation.x) == Bits(other.spawnRotation.x) &&
                   Bits(spawnRotation.y) == Bits(other.spawnRotation.y) &&
                   Bits(spawnRotation.z) == Bits(other.spawnRotation.z) &&
                   Bits(spawnRotation.w) == Bits(other.spawnRotation.w);
        }

        private static bool SameOwner(PlayerID? a, PlayerID? b)
            => a.HasValue == b.HasValue && (!a.HasValue ||
                a.Value.id.value == b.Value.id.value && a.Value.isBot == b.Value.isBot);

        private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

        public override bool Equals(object obj)
        {
            return obj is InstanceDetails other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(prefabId, pieceIndex, instanceId, spawnPosition, spawnRotation, owner, parent, networkId);
        }

        public override string ToString()
        {
            return $"id: {instanceId} (piece {pieceIndex.value} of {rootId}), {spawnPosition} | {spawnRotation}{(parent.HasValue ? $" | parent: {parent.Value}" : string.Empty)}{(networkId.HasValue ? $" | nid: {networkId.Value}" : string.Empty)}\n";
        }
    }
}

using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    internal readonly struct PrototypePiece
    {
        public readonly int parentPieceIndex;
        public readonly int[] inverseSiblingPath;
        public readonly Vector3 localPosition;
        public readonly Quaternion localRotation;
        public readonly Vector3 localScale;
        public readonly bool activeSelf;

        /// <summary>NetworkIdentity components owned by this piece, in collection order.</summary>
        public readonly int networkIdentityCount;

        /// <summary>Offset of this piece's first NetworkIdentity inside the instance's id block.</summary>
        public readonly int networkIdOffset;

        public PrototypePiece(int parentPieceIndex, int[] inverseSiblingPath, Vector3 localPosition,
            Quaternion localRotation, Vector3 localScale, bool activeSelf, int networkIdentityCount = 0, int networkIdOffset = 0)
        {
            this.parentPieceIndex = parentPieceIndex;
            this.inverseSiblingPath = inverseSiblingPath;
            this.localPosition = localPosition;
            this.localRotation = localRotation;
            this.localScale = localScale;
            this.activeSelf = activeSelf;
            this.networkIdentityCount = networkIdentityCount;
            this.networkIdOffset = networkIdOffset;
        }
    }

    internal sealed class PiecePrototype
    {
        public readonly PrototypePiece[] pieces;

        /// <summary>Total NetworkIdentity components across all pieces; the size of one id block.</summary>
        public readonly int networkIdentityCount;

        public int pieceCount => pieces.Length;

        private PiecePrototype(PrototypePiece[] pieces)
        {
            this.pieces = pieces;
            for (var i = 0; i < pieces.Length; i++)
                networkIdentityCount += pieces[i].networkIdentityCount;
        }

        /// <summary>
        /// Collects the NetworkIdentity components a piece owns: those on the piece itself and on
        /// descendants that are not pieces of their own. Order is the deterministic hierarchy
        /// order, so every peer maps the same component to the same id offset.
        /// </summary>
        public static void CollectNetworkIdentities(Transform piece, List<NetworkIdentity> result)
        {
            var own = ListPool<NetworkIdentity>.Instantiate();
            piece.GetComponents(own);
            result.AddRange(own);
            ListPool<NetworkIdentity>.Destroy(own);

            int childCount = piece.childCount;
            for (var i = 0; i < childCount; i++)
            {
                var child = piece.GetChild(i);
                if (child.TryGetComponent<PredictedIdentity>(out _))
                    continue;
                CollectNetworkIdentities(child, result);
            }
        }

        public static PiecePrototype Build(GameObject root, HashSet<Transform> boundaries = null)
        {
            if (!root.TryGetComponent<PredictedIdentity>(out _))
            {
                PurrLogger.LogError($"'{root.name}' has no PredictedIdentity on its root; the root must carry at least one predicted component.", root);
                return null;
            }

            var result = new List<PrototypePiece>();
            BuildRecursive(root.transform, -1, result, boundaries);
            return new PiecePrototype(result.ToArray());
        }

        static void BuildRecursive(Transform current, int parentPieceIndex, List<PrototypePiece> result, HashSet<Transform> boundaries)
        {
            int ownPieceIndex = parentPieceIndex;

            if (current.TryGetComponent<PredictedIdentity>(out _))
            {
                ownPieceIndex = result.Count;

                var path = parentPieceIndex < 0 ? System.Array.Empty<int>() : GetInverseSiblingPath(current, parentPieceIndex, result);

                var networkIdentities = ListPool<NetworkIdentity>.Instantiate();
                CollectNetworkIdentities(current, networkIdentities);
                int networkIdentityCount = networkIdentities.Count;
                ListPool<NetworkIdentity>.Destroy(networkIdentities);

                int networkIdOffset = 0;
                for (var i = 0; i < result.Count; i++)
                    networkIdOffset += result[i].networkIdentityCount;

                result.Add(new PrototypePiece(
                    parentPieceIndex,
                    path,
                    current.localPosition,
                    current.localRotation,
                    current.localScale,
                    current.gameObject.activeSelf,
                    networkIdentityCount,
                    networkIdOffset));
            }

            int childCount = current.childCount;
            for (var i = 0; i < childCount; i++)
            {
                var child = current.GetChild(i);

                if (boundaries != null && boundaries.Contains(child))
                    continue;

                BuildRecursive(child, ownPieceIndex, result, boundaries);
            }
        }

        static int[] GetInverseSiblingPath(Transform piece, int parentPieceIndex, List<PrototypePiece> builtSoFar)
        {
            int depth = 0;
            var current = piece;

            while (current.parent != null)
            {
                depth++;
                current = current.parent;
                if (HasPieceAtDepth(current))
                    break;
            }

            var path = new int[depth];
            current = piece;

            for (var i = 0; i < depth; i++)
            {
                path[i] = current.GetSiblingIndex();
                current = current.parent;
            }

            return path;

            static bool HasPieceAtDepth(Transform t) => t.TryGetComponent<PredictedIdentity>(out _);
        }

        public static void AttachAtPath(Transform parent, Transform piece, int[] inversedPath, bool worldPositionStays)
        {
            if (inversedPath == null || inversedPath.Length == 0)
            {
                piece.SetParent(parent, worldPositionStays);
                return;
            }

            int len = inversedPath.Length;
            for (var i = len - 1; i >= 1; i--)
            {
                var siblingIndex = inversedPath[i];

                if (parent.childCount <= siblingIndex)
                {
                    PurrLogger.LogWarning($"Parent '{parent.name}' has no child at index {siblingIndex}; attaching '{piece.name}' directly.");
                    break;
                }

                parent = parent.GetChild(siblingIndex);
            }

            piece.SetParent(parent, worldPositionStays);

            var targetSiblingIndex = inversedPath[0];

            if (parent.childCount <= targetSiblingIndex)
                targetSiblingIndex = parent.childCount;

            piece.SetSiblingIndex(targetSiblingIndex);
        }

        public bool TryCollectInstancePieces(GameObject instanceRoot, List<GameObject> results, HashSet<Transform> boundaries = null)
        {
            results.Clear();
            CollectRecursive(instanceRoot.transform, results, boundaries);

            if (results.Count != pieces.Length)
            {
                PurrLogger.LogError($"'{instanceRoot.name}' has {results.Count} predicted pieces but its prototype expects {pieces.Length}; the instance no longer matches the authored asset.", instanceRoot);
                return false;
            }

            return true;
        }

        static void CollectRecursive(Transform current, List<GameObject> results, HashSet<Transform> boundaries)
        {
            if (current.TryGetComponent<PredictedIdentity>(out _))
                results.Add(current.gameObject);

            int childCount = current.childCount;
            for (var i = 0; i < childCount; i++)
            {
                var child = current.GetChild(i);

                if (boundaries != null && boundaries.Contains(child))
                    continue;

                CollectRecursive(child, results, boundaries);
            }
        }
    }
}

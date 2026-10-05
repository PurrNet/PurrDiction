using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// A pooled entry claims every piece id it holds. When a create for one of those ids is
    /// satisfied from somewhere else - the fuzzy fallback returning a different tree, or a fresh
    /// instantiate - the claim must not survive, or the pool will later hand out a stale
    /// GameObject for an id that is already live.
    /// </summary>
    public sealed class PiecePoolClaimTests
    {
        private readonly List<GameObject> _tracked = new();

        [TearDown]
        public void Cleanup()
        {
            for (var i = 0; i < _tracked.Count; i++)
            {
                if (_tracked[i])
                    Object.DestroyImmediate(_tracked[i]);
            }
            _tracked.Clear();
        }

        private GameObject Track(GameObject go)
        {
            _tracked.Add(go);
            return go;
        }

        private static List<PooledPiece> Single(PredictedObjectID id, GameObject go)
            => new() { new PooledPiece(id, 0, go) };

        [Test]
        public void ReleasingAClaimAfterAFallbackServedADifferentTree()
        {
            var pool = new PredictedPiecePool();

            var exact = Track(new GameObject("exact"));
            pool.PutTree(1, new PredictedObjectID(50), Vector3.zero, exact,
                Single(new PredictedObjectID(50), exact), 0, true);

            var nearer = Track(new GameObject("nearer"));
            pool.PutTree(1, new PredictedObjectID(60), new Vector3(5, 0, 0), nearer,
                Single(new PredictedObjectID(60), nearer), 0, true);

            var taken = new List<PooledPiece>();
            var respawnPosition = new Vector3(5, 0, 0);

            Assert.That(
                pool.TryTakeTree(new PredictedObjectID(50), 1, respawnPosition, true, taken, out _, out var drifted),
                Is.False);
            Assert.That(drifted, Is.True);

            taken.Clear();
            Assert.That(
                pool.TryTakeNearestCompleteTree(1, respawnPosition, taken, out var served),
                Is.True);
            Assert.That(served, Is.EqualTo(nearer),
                "the fallback served the create from a different pooled tree");

            // Id 50 is now live as `nearer`, which is what the hierarchy registers it as.
            pool.ReleaseClaim(new PredictedObjectID(50));

            Assert.That(pool.Contains(new PredictedObjectID(50)), Is.False,
                "the pool must not still claim an id that is live elsewhere");
            Assert.That(pool.TryTakePiece(new PredictedObjectID(50), 1, out _), Is.False,
                "a released id must never yield a stale GameObject");

            // The bypassed tree keeps its GameObject so it is still reusable and still torn
            // down with its entry - releasing a claim must not leak it.
            taken.Clear();
            Assert.That(pool.TryTakeNearestCompleteTree(1, Vector3.zero, taken, out var reused), Is.True);
            Assert.That(reused, Is.EqualTo(exact),
                "releasing the id claim must not orphan the pooled GameObject");
        }

        [Test]
        public void ReleasingANonRootClaimAfterAFreshInstantiate()
        {
            var pool = new PredictedPiecePool();

            var root = Track(new GameObject("root"));
            var child = Track(new GameObject("child"));
            child.transform.SetParent(root.transform);

            var pieces = new List<PooledPiece>
            {
                new PooledPiece(new PredictedObjectID(70), 0, root),
                new PooledPiece(new PredictedObjectID(71), 1, child)
            };
            pool.PutTree(1, new PredictedObjectID(70), Vector3.zero, root, pieces, 0, true);

            var taken = new List<PooledPiece>();

            // A create whose root record is the child id: the pool maps 71, but not as a root,
            // so the exact take fails without reporting drift and the caller falls through to a
            // fresh instantiate.
            Assert.That(
                pool.TryTakeTree(new PredictedObjectID(71), 1, Vector3.zero, true, taken, out _, out var drifted),
                Is.False);
            Assert.That(drifted, Is.False,
                "a non-root claim must not be reported as drift, so there is no fuzzy fallback");

            // The caller instantiates fresh and registers 71, so the claim must be relinquished.
            pool.ReleaseClaim(new PredictedObjectID(71));

            Assert.That(pool.Contains(new PredictedObjectID(71)), Is.False);
            Assert.That(pool.TryTakePiece(new PredictedObjectID(71), 1, out _), Is.False,
                "a released non-root id must never yield the pooled child");

            Assert.That(pool.Contains(new PredictedObjectID(70)), Is.True,
                "releasing one piece must not disturb the rest of the entry");
            Assert.That(pool.TryTakePiece(new PredictedObjectID(70), 1, out var takenRoot), Is.True);
            Assert.That(takenRoot, Is.EqualTo(root));
        }

        [Test]
        public void DriftedExactCompleteTreeIsAvailableBeforeAnUnrelatedNearerTree()
        {
            var pool = new PredictedPiecePool();
            var id = new PredictedObjectID(80);
            var otherId = new PredictedObjectID(90);
            var exact = Track(new GameObject("exact drifted tree"));
            var nearer = Track(new GameObject("unrelated nearer tree"));
            var replayPosition = new Vector3(100, 0, 0);
            pool.PutTree(1, id, Vector3.zero, exact, Single(id, exact), 0, true);
            pool.PutTree(1, otherId, replayPosition, nearer, Single(otherId, nearer), 0, true);

            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeTree(id, 1, replayPosition, true, taken, out _, out var drifted), Is.False);
            Assert.That(drifted, Is.True);
            Assert.That(pool.TryTakeExactCompleteTree(id, 1, taken, out var served), Is.True);
            Assert.That(served, Is.SameAs(exact), "pose drift must not change which logical identity is restored");
            Assert.That(taken.Count, Is.EqualTo(1));
            Assert.That(taken[0].id, Is.EqualTo(id));
            Assert.That(pool.Contains(id), Is.False);
            Assert.That(pool.Contains(otherId), Is.True, "the unrelated candidate must remain available for its own replay");

            taken.Clear();
            Assert.That(pool.TryTakeTree(otherId, 1, replayPosition, true, taken, out var untouched, out _), Is.True);
            Assert.That(untouched, Is.SameAs(nearer));
        }

        [Test]
        public void ExactCompleteTakeTransfersEveryPieceWithoutLeavingClaims()
        {
            var pool = new PredictedPiecePool();
            var root = Track(new GameObject("complete root"));
            var child = Track(new GameObject("complete child"));
            var leaf = Track(new GameObject("complete leaf"));
            child.transform.SetParent(root.transform);
            leaf.transform.SetParent(child.transform);
            var pieces = new List<PooledPiece>
            {
                new(new PredictedObjectID(100), 0, root),
                new(new PredictedObjectID(101), 1, child),
                new(new PredictedObjectID(102), 2, leaf)
            };
            pool.PutTree(1, pieces[0].id, Vector3.zero, root, pieces, 0, true);

            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeExactCompleteTree(pieces[0].id, 1, taken, out var served), Is.True);
            Assert.That(served, Is.SameAs(root));
            Assert.That(taken.Count, Is.EqualTo(pieces.Count));
            for (int i = 0; i < pieces.Count; i++)
            {
                Assert.That(taken[i].id, Is.EqualTo(pieces[i].id));
                Assert.That(taken[i].pieceIndex, Is.EqualTo(pieces[i].pieceIndex));
                Assert.That(taken[i].gameObject, Is.SameAs(pieces[i].gameObject));
                Assert.That(pool.Contains(pieces[i].id), Is.False);
                Assert.That(pool.TryTakePiece(pieces[i].id, 1, out _), Is.False,
                    "a transferred piece cannot later be handed out a second time");
            }
            pool.Clear(null);
            Assert.That(root && child && leaf, Is.True, "pool cleanup no longer owns any transferred GameObject");
        }

        [TestCase("newId")]
        [TestCase("wrongPrefab")]
        [TestCase("nonRoot")]
        [TestCase("partial")]
        public void ExactCompleteTakeRejectsIneligibleClaimsWithoutConsumingThem(string mismatch)
        {
            var pool = new PredictedPiecePool();
            var rootId = new PredictedObjectID(110);
            var childId = new PredictedObjectID(111);
            var root = Track(new GameObject("guard root"));
            var child = Track(new GameObject("guard child"));
            child.transform.SetParent(root.transform);
            var pieces = new List<PooledPiece>
            {
                new(rootId, 0, root),
                new(childId, 1, child)
            };
            pool.PutTree(1, rootId, Vector3.zero, root, pieces, 0, mismatch != "partial");
            var requestedId = mismatch == "newId" ? new PredictedObjectID(120) :
                mismatch == "nonRoot" ? childId : rootId;
            int prefab = mismatch == "wrongPrefab" ? 2 : 1;
            var taken = new List<PooledPiece>();

            Assert.That(pool.TryTakeExactCompleteTree(requestedId, prefab, taken, out var served), Is.False);
            Assert.That(served, Is.Null);
            Assert.That(taken, Is.Empty);
            Assert.That(pool.Contains(rootId), Is.True);
            Assert.That(pool.Contains(childId), Is.True);
            Assert.That(pool.TryTakePiece(childId, 1, out var stillClaimed), Is.True,
                "an ineligible whole-tree request must leave exact piece recovery intact");
            Assert.That(stillClaimed, Is.SameAs(child));
        }

        [Test]
        public void ExtractingAPiecePreventsCompleteReuseOfEitherRemainingSubtree()
        {
            var pool = new PredictedPiecePool();
            var root = Track(new GameObject("split root"));
            var child = Track(new GameObject("split child"));
            var leaf = Track(new GameObject("split leaf"));
            child.transform.SetParent(root.transform);
            leaf.transform.SetParent(child.transform);
            var rootId = new PredictedObjectID(130);
            var childId = new PredictedObjectID(131);
            var leafId = new PredictedObjectID(132);
            pool.PutTree(1, rootId, Vector3.zero, root, new List<PooledPiece>
            {
                new(rootId, 0, root), new(childId, 1, child), new(leafId, 2, leaf)
            }, 0, true);

            Assert.That(pool.TryTakePiece(childId, 1, out var extracted), Is.True);
            Assert.That(extracted, Is.SameAs(child));
            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeExactCompleteTree(rootId, 1, taken, out _), Is.False);
            Assert.That(pool.TryTakeExactCompleteTree(leafId, 1, taken, out _), Is.False);
            Assert.That(pool.TryTakeNearestCompleteTree(1, Vector3.zero, taken, out _), Is.False,
                "partial remains cannot serve as an unrelated complete prefab either");
            Assert.That(taken, Is.Empty);
            Assert.That(pool.TryTakePiece(rootId, 1, out var remainingRoot), Is.True);
            Assert.That(pool.TryTakePiece(leafId, 1, out var remainingLeaf), Is.True);
            Assert.That(remainingRoot, Is.SameAs(root));
            Assert.That(remainingLeaf, Is.SameAs(leaf));
        }

        [Test]
        public void TakingAReleasedOldTreeDoesNotEraseANewerClaimForTheSameId()
        {
            var pool = new PredictedPiecePool();
            var id = new PredictedObjectID(140);
            var old = Track(new GameObject("released old tree"));
            var replacement = Track(new GameObject("new claimant"));
            pool.PutTree(1, id, Vector3.zero, old, Single(id, old), 0, true);
            pool.ReleaseClaim(id);
            pool.PutTree(1, id, new Vector3(10, 0, 0), replacement, Single(id, replacement), 1, true);

            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeNearestCompleteTree(1, Vector3.zero, taken, out var served), Is.True);
            Assert.That(served, Is.SameAs(old));
            Assert.That(pool.Contains(id), Is.True,
                "removing an old entry may release only claims that still point to that entry");
            Assert.That(pool.TryTakePiece(id, 1, out var current), Is.True);
            Assert.That(current, Is.SameAs(replacement));
            pool.Clear(null);
            Assert.That(old && replacement, Is.True);
        }

        [Test]
        public void ClearingAReleasedPartialEntryDoesNotDestroyTheLiveReplacement()
        {
            var pool = new PredictedPiecePool();
            var id = new PredictedObjectID(150);
            var old = Track(new GameObject("released partial piece"));
            var replacement = Track(new GameObject("live replacement piece"));
            pool.PutPiece(1, id, 0, old, 0);
            pool.ReleaseClaim(id);
            pool.PutPiece(1, id, 0, replacement, 1);
            Assert.That(pool.TryTakePiece(id, 1, out var current), Is.True);
            Assert.That(current, Is.SameAs(replacement));

            // Only the old partial entry remains pool-owned. This path uses direct
            // destruction, so no manager, prefab asset or transport fixture is required.
            pool.Clear(null);
            Assert.That(old == null, Is.True, "the bypassed physical piece must be cleaned up");
            Assert.That(replacement != null, Is.True, "the transferred replacement is live and no longer pool-owned");
            Assert.That(pool.Contains(id), Is.False);
        }

    }
}

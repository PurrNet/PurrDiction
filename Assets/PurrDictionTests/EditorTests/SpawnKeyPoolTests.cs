using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// A client's own spawn keeps its spawn key (tick, creator, ordinal) when spawns it could not
    /// predict, such as other players' shots, shift its instance id. The pool must hand the re-created
    /// spawn the GameObject that showed it before, and its fallbacks must not give that GameObject to
    /// another object while the replay has yet to re-create it.
    /// </summary>
    public sealed class SpawnKeyPoolTests
    {
        private static readonly PredictedComponentID Shooter = new(new PredictedObjectID(3), 0);
        private static readonly PredictedComponentID OtherShooter = new(new PredictedObjectID(4), 0);

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

        private GameObject Put(PredictedPiecePool pool, uint id, in SpawnKey key, Vector3 position = default)
        {
            var go = new GameObject($"pooled-{id}");
            _tracked.Add(go);
            var pieceId = new PredictedObjectID(id);
            pool.PutTree(1, pieceId, position, go, new List<PooledPiece> { new(pieceId, 0, go) }, 0, true, key);
            return go;
        }

        [Test]
        public void ShiftedIdTakesTheTreeThatShowedTheSameSpawn()
        {
            var pool = new PredictedPiecePool();
            var firstShot = Put(pool, 75, new SpawnKey(288, Shooter, 0));
            Put(pool, 76, new SpawnKey(292, Shooter, 0));

            // The server created another player's shot first, so this client's tick-288 shot is id 76.
            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeSameSpawn(1, new SpawnKey(288, Shooter, 0), new PredictedObjectID(76), taken, out var go),
                Is.True);
            Assert.That(go, Is.EqualTo(firstShot), "the tree that showed the tick-288 shot, whatever its old id");
        }

        [Test]
        public void SameSpawnPrefersTheTreeThatKeptItsId()
        {
            var pool = new PredictedPiecePool();
            Put(pool, 75, new SpawnKey(288, Shooter, 0));
            var keptId = Put(pool, 76, new SpawnKey(288, Shooter, 0));

            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeSameSpawn(1, new SpawnKey(288, Shooter, 0), new PredictedObjectID(76), taken, out var go),
                Is.True);
            Assert.That(go, Is.EqualTo(keptId));
        }

        [Test]
        public void DifferentCreatorOrOrdinalIsADifferentSpawn()
        {
            var pool = new PredictedPiecePool();
            Put(pool, 75, new SpawnKey(288, Shooter, 0));

            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeSameSpawn(1, new SpawnKey(288, OtherShooter, 0), new PredictedObjectID(75), taken, out _),
                Is.False);
            Assert.That(pool.TryTakeSameSpawn(1, new SpawnKey(288, Shooter, 1), new PredictedObjectID(75), taken, out _),
                Is.False);
            Assert.That(pool.TryTakeSameSpawn(1, default, new PredictedObjectID(75), taken, out _), Is.False,
                "a spawn made outside any identity's simulation has no key to match");
        }

        [Test]
        public void FallbacksLeaveSpawnsTheReplayStillHasToRecreate()
        {
            var pool = new PredictedPiecePool();
            var laterShot = Put(pool, 76, new SpawnKey(292, Shooter, 0));

            // At tick 288 another object takes id 76; the tick-292 shot is re-created later this replay.
            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeTree(new PredictedObjectID(76), 1, Vector3.zero, true, taken, out _, out _, 288), Is.False);
            Assert.That(pool.TryTakeExactCompleteTree(new PredictedObjectID(76), 1, taken, out _, 288), Is.False);
            Assert.That(pool.TryTakeNearestCompleteTree(1, Vector3.zero, taken, out _, 288), Is.False);

            // Once the replay is past its tick without re-creating it, it is ordinary pooled stock.
            Assert.That(pool.TryTakeNearestCompleteTree(1, Vector3.zero, taken, out var go, 293), Is.True);
            Assert.That(go, Is.EqualTo(laterShot));
        }

        [Test]
        public void KeylessEntriesStayAvailableToFallbacks()
        {
            var pool = new PredictedPiecePool();
            var keyless = Put(pool, 76, default);

            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeTree(new PredictedObjectID(76), 1, Vector3.zero, true, taken, out var go, out _, 288),
                Is.True);
            Assert.That(go, Is.EqualTo(keyless));
        }
    }
}

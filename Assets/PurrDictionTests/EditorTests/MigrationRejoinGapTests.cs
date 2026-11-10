using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// A player from the old session joins a promoted host a few frames before it observes the PredictionManager
    /// (it still has to load the scene). PredictedPlayers must keep them through that gap instead of firing
    /// onPlayerRemoved and adding them again.
    /// </summary>
    public sealed class MigrationRejoinGapTests
    {
        private const BindingFlags Members =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private GameObject _networkObject;
        private GameObject _managerObject;
        private NetworkManager _networkManager;
        private PredictionManager _manager;

        private static readonly PlayerID Returning = new PlayerID(2, false);

        [SetUp]
        public void SetUp()
        {
            _networkObject = new GameObject("Migration rejoin network");
            _managerObject = new GameObject("Migration rejoin manager");
            _networkManager = _networkObject.AddComponent<NetworkManager>();

            var tickManager = new TickManager(20, _networkManager, null, false);
            SetField(typeof(NetworkManager), _networkManager, "_clientTickManager", tickManager);

            _manager = _managerObject.AddComponent<PredictionManager>();
            SetField(typeof(NetworkIdentity), _manager, "<networkManager>k__BackingField", _networkManager);
            SetField(typeof(PredictionManager), _manager, "<tickRate>k__BackingField", 20);
            _manager.SetIsSpawned(true, false);
            Invoke(typeof(PredictionManager), _manager, "HookMigrationRoster", _networkManager);

            GetField<List<PlayerID>>(typeof(NetworkManager), _networkManager, "_hostMigrationPendingPlayers")
                .Add(Returning);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_managerObject);
            Object.DestroyImmediate(_networkObject);
        }

        [Test]
        public void ReturningPlayerStaysPresentUntilItObserves()
        {
            Assert.That(_manager.IsAwaitingMigrationRejoin(Returning), Is.True, "pending before it reconnects");

            ReturnPlayer();
            Assert.That(_networkManager.IsHostMigrationPlayerPending(Returning), Is.False);
            Assert.That(_manager.IsObserver(Returning), Is.False);
            Assert.That(_manager.IsAwaitingMigrationRejoin(Returning), Is.True,
                "joined but not observing yet: PredictedPlayers would remove it");

            Invoke(typeof(NetworkIdentity), _manager, "TryAddObserver", Returning);
            Assert.That(_manager.IsAwaitingMigrationRejoin(Returning), Is.False, "observing ends the wait");

            Invoke(typeof(NetworkIdentity), _manager, "TryRemoveObserver", Returning);
            Assert.That(_manager.IsAwaitingMigrationRejoin(Returning), Is.False,
                "a later observer loss is an ordinary departure");
        }

        [Test]
        public void ReturningPlayerThatLeavesAgainIsGone()
        {
            ReturnPlayer();
            Assert.That(_manager.IsAwaitingMigrationRejoin(Returning), Is.True);

            Invoke(typeof(NetworkManager), _networkManager, "OnPlayerLeft", Returning, true);
            Assert.That(_manager.IsAwaitingMigrationRejoin(Returning), Is.False);
        }

        [Test]
        public void ReturningPlayerIsNotWaitedForPastTheRejoinTimeout()
        {
            ReturnPlayer();
            var rejoiners = GetField<Dictionary<PlayerID, double>>(
                typeof(PredictionManager), _manager, "_migrationRejoiners");
            Assert.That(rejoiners[Returning], Is.EqualTo(Time.unscaledTimeAsDouble + _networkManager.hostMigrationRejoinTimeout)
                .Within(1e-3));

            rejoiners[Returning] = Time.unscaledTimeAsDouble - 1d;
            Assert.That(_manager.IsAwaitingMigrationRejoin(Returning), Is.False);
        }

        [Test]
        public void UnrelatedPlayerIsNotAwaited()
        {
            ReturnPlayer();
            Assert.That(_manager.IsAwaitingMigrationRejoin(new PlayerID(3, false)), Is.False);
        }

        private void ReturnPlayer()
            => Invoke(typeof(NetworkManager), _networkManager, "NoteHostMigrationPlayerJoined", Returning);

        private static void Invoke(Type declaringType, object target, string methodName, params object[] args)
        {
            var method = declaringType.GetMethod(methodName, Members);
            Assert.That(method, Is.Not.Null, $"Missing method {declaringType.FullName}.{methodName}");
            method.Invoke(target, args);
        }

        private static T GetField<T>(Type declaringType, object target, string fieldName)
        {
            var field = declaringType.GetField(fieldName, Members);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            return (T)field.GetValue(target);
        }

        private static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, Members);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }
    }
}

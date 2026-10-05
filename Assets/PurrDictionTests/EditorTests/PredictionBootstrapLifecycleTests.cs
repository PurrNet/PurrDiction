using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionBootstrapLifecycleTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _object;
        private PredictionBootstrap _bootstrap;
        private BootstrapLifecycleProbe _probe;
        private CancellationTokenSource _outerCancellation;
        private ScenarioDetails?[] _results;

        [SetUp]
        public void SetUp()
        {
            ScenarioSynchronization.Reset();
            _outerCancellation = new CancellationTokenSource();
            _object = new GameObject(nameof(PredictionBootstrapLifecycleTests));
            // Exercise RunOne without Awake/Start opening a network session or creating the suite.
            _object.SetActive(false);
            _bootstrap = _object.AddComponent<PredictionBootstrap>();
            _probe = _object.AddComponent<BootstrapLifecycleProbe>();
            _results = new ScenarioDetails?[1];
            SetField("_scenarios", new Scenario[] { _probe });
            SetField("_results", _results);
            Invoke("OnEnable");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (_bootstrap)
                    Invoke("OnDisable");
                if (_probe)
                    _probe.registration.Dispose();
                if (_object)
                    Object.DestroyImmediate(_object);
            }
            finally
            {
                _outerCancellation?.Dispose();
                ScenarioSynchronization.Reset();
            }
        }

        [Test]
        public void ThrowingPrepareRecordsFailureClosesEpochAndCancelsOnlyItsCopiedToken()
        {
            _probe.throwDuringPrepare = true;
            LogAssert.Expect(LogType.Error,
                new Regex("Scenario \\[0\\].*intentional prepare failure", RegexOptions.Singleline));

            var failed = RunOne();

            Assert.That(failed, Is.True);
            Assert.That(_probe.runCalls, Is.Zero, "RunScenario must not execute after PrepareRun fails");
            AssertFailureAndCleanup("intentional prepare failure");
        }

        [Test]
        public void ThrowingCancellationCallbackStillRecordsFailureAndClosesEpoch()
        {
            _probe.throwDuringCancellation = true;
            LogAssert.Expect(LogType.Exception,
                new Regex("InvalidOperationException: intentional cancellation failure"));

            var failed = RunOne();

            Assert.That(failed, Is.True, "a successful scenario must fail if its cleanup throws");
            Assert.That(_probe.runCalls, Is.EqualTo(1));
            Assert.That(_probe.cancellationCalls, Is.EqualTo(1));
            AssertFailureAndCleanup("scenario cleanup failed");
            Assert.That(_results[0].Value.result.message, Does.Contain("intentional cancellation failure"));
        }

        private bool RunOne()
        {
            var context = new ScenarioContext
            {
                role = NetworkRole.Server,
                scenarioIndex = 99,
                cancellationToken = _outerCancellation.Token
            };
            var pending = (UniTask<bool>)Invoke("RunOne", 0, context, 123UL);
            // The probe completes synchronously; this test needs no Editor player-loop pumping.
            Assert.That(pending.Status, Is.EqualTo(UniTaskStatus.Succeeded));
            var failed = pending.GetAwaiter().GetResult();
            Assert.That(context.scenarioIndex, Is.EqualTo(99));
            Assert.That(context.cancellationToken, Is.EqualTo(_outerCancellation.Token));
            return failed;
        }

        private void AssertFailureAndCleanup(string expectedMessage)
        {
            Assert.That(_results[0].HasValue, Is.True, "RunOne must retain a reviewable failure result");
            var details = _results[0].Value;
            Assert.That(details.name, Is.EqualTo(nameof(BootstrapLifecycleProbe)));
            Assert.That(details.result.success, Is.False);
            Assert.That(details.result.message, Does.Contain(expectedMessage));
            Assert.That(_probe.capturedContext.scenarioIndex, Is.Zero);
            Assert.That(_probe.startTick, Is.EqualTo(123UL));
            Assert.That(_probe.capturedContext.cancellationToken, Is.Not.EqualTo(_outerCancellation.Token));
            Assert.That(_probe.capturedContext.cancellationToken.IsCancellationRequested, Is.True);
            Assert.That(_outerCancellation.IsCancellationRequested, Is.False);
            Assert.That(ScenarioSynchronization.IsOpen(0), Is.False);
            Assert.That(ScenarioSynchronization.IsOpen(1), Is.True);
            Assert.That(GetField("_activeScenarioIndex"), Is.EqualTo(-1));
            Assert.Throws<InvalidOperationException>(() => ScenarioSynchronization.BeginScenario(0));
        }

        private object Invoke(string name, params object[] arguments)
        {
            var method = typeof(PredictionBootstrap).GetMethod(name, PrivateInstance);
            Assert.That(method, Is.Not.Null, name);
            return method.Invoke(_bootstrap, arguments);
        }

        private void SetField(string name, object value)
            => typeof(PredictionBootstrap).GetField(name, PrivateInstance).SetValue(_bootstrap, value);

        private object GetField(string name)
            => typeof(PredictionBootstrap).GetField(name, PrivateInstance).GetValue(_bootstrap);
    }

    public sealed class BootstrapLifecycleProbe : Scenario
    {
        public bool throwDuringPrepare;
        public bool throwDuringCancellation;
        public ScenarioContext capturedContext;
        public ulong startTick;
        public int runCalls;
        public int cancellationCalls;
        public CancellationTokenRegistration registration;

        public override void PrepareRun(ScenarioContext ctx, ulong scheduledStartTick)
        {
            capturedContext = ctx;
            startTick = scheduledStartTick;
            if (throwDuringCancellation)
            {
                registration = ctx.cancellationToken.Register(() =>
                {
                    cancellationCalls++;
                    throw new InvalidOperationException("intentional cancellation failure");
                });
            }
            if (throwDuringPrepare)
                throw new InvalidOperationException("intentional prepare failure");
        }

        public override UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
        {
            runCalls++;
            return UniTask.FromResult(ScenarioResult.Ok("probe completed"));
        }
    }
}

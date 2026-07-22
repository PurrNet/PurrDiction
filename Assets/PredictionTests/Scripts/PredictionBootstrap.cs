using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PurrNet;
using PurrNet.Prediction;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.Assertions;

public class PredictionBootstrap : Scenario
{
    private const int MaxUnexpectedLogsPerScenario = 8;
    private const int MaxUnexpectedLogLength = 700;
    private const float ScenarioStartLeadSeconds = 2f;

    [SerializeField] private NetworkManager _networkManager;
    [SerializeField] private PredictionManager _predictionManager;
    [SerializeField] private float _connectionTimeout = 180f;
    [SerializeField] private float _timeBetweenScenarios = 0.1f;

    [Header("Editor overrides (used when -role / -count are absent)")]
    [SerializeField] private NetworkRole _editorRole = NetworkRole.Host;
    [SerializeField] private int _editorExpectedConnections = 2;

    [Header("Network simulation (overridden by -latencyMin / -latencyMax)")]
    [SerializeField] private bool _simulateLatency = true;
    [SerializeField] private int _packetLossChance;
    [SerializeField] private int _minLatencyMs = 40;
    [SerializeField] private int _maxLatencyMs = 80;

    private NetworkRole _role;
    private int _expectedConnections;
    private string _resultsPath;
    private string _serverHost;
    private ushort? _port;
    private bool _profileScenarios;

    private Scenario[] _scenarios;
    private ScenarioDetails?[] _results;
    private readonly List<string> _unexpectedLogs = new();
    private int _activeScenarioIndex = -1;
    private ScenarioPerformanceSampler _activePerformanceSampler;

    private CancellationTokenSource _runCts;

    private void Awake()
    {
        ScenarioSequencer.Reset();
        DigestGate.Reset();

        var prefabs = ScriptableObject.CreateInstance<PredictedPrefabs>();
        prefabs.autoGenerate = false;
        prefabs.searchAllIfNoFolder = false;
        _predictionManager.predictedPrefabs = prefabs;

        LoadArgs();

        if (CommandLineUtils.HasFlag("-serverLoadBenchmark"))
        {
            _scenarios = new Scenario[]
            {
                this,
                gameObject.AddComponent<ServerLoadBenchmarkScenario>()
            };
            _results = new ScenarioDetails?[_scenarios.Length];
            return;
        }

        if (CommandLineUtils.HasFlag("-interestScenariosOnly"))
        {
            _scenarios = new Scenario[]
            {
                this,
                gameObject.AddComponent<InterestManagementScenario>()
            };
            _results = new ScenarioDetails?[_scenarios.Length];
            return;
        }

        if (CommandLineUtils.HasFlag("-includeHistoryStressScenario"))
            gameObject.AddComponent<HistoryStressScenario>();

        gameObject.AddComponent<PieceLifecycleScenario>();
        gameObject.AddComponent<PieceReconnectScenario>();
        gameObject.AddComponent<TickAgreementScenario>();
        gameObject.AddComponent<DeterministicGauntletScenario>();

        bool policyRegressionsOnly = CommandLineUtils.HasFlag("-policyRegressionScenariosOnly");
        var policyRegressionScenarios = AddPolicyRegressionScenarios();

        if (policyRegressionsOnly)
        {
            _scenarios = new Scenario[policyRegressionScenarios.Length + 1];
            _scenarios[0] = this;
            Array.Copy(policyRegressionScenarios, 0, _scenarios, 1, policyRegressionScenarios.Length);
        }
        else
        {
            _scenarios = GetComponentsInChildren<Scenario>();
        }

        _results = new ScenarioDetails?[_scenarios.Length];
    }

    private Scenario[] AddPolicyRegressionScenarios()
    {
        return new Scenario[]
        {
            gameObject.AddComponent<SoftCorrectionPoolReuseScenario>(),
            gameObject.AddComponent<GenericSoftCorrectionScenario>(),
            gameObject.AddComponent<ReplayPolicyTransitionScenario>()
        };
    }

    private void OnEnable()
    {
        Application.logMessageReceived += OnLogMessageReceived;
    }

    private void OnDisable()
    {
        Application.logMessageReceived -= OnLogMessageReceived;
    }

    private void Start()
    {
        if (!_networkManager.transport)
        {
            Debug.LogError("No network transport found");
            Application.Quit(-1);
            return;
        }

        _runCts = new CancellationTokenSource();

        ConfigureTransport();

        var ctx = MakeContext();

        for (var i = 0; i < _scenarios.Length; i++)
            _scenarios[i].Setup(ctx, _networkManager);

        SubscribeToDataSent(_networkManager.transport.transport);
        Debug.Log($"[PredictionTests] Starting {_role} run with {_scenarios.Length} scenarios; expectedConnections={_expectedConnections}");

        RunScenarios();
    }

    private ScenarioContext MakeContext()
    {
        return new ScenarioContext
        {
            role = _role,
            expectedConnections = _expectedConnections,
            networkManager = _networkManager,
            predictionManager = _predictionManager,
            cancellationToken = _runCts.Token
        };
    }

    private ITransport _lastTransport;

    private void SubscribeToDataSent(ITransport transport)
    {
        if (_lastTransport != null)
        {
            _lastTransport.onDataSent -= OnDataSentCallback;
            _lastTransport.onDataReceived -= OnDataReceivedCallback;
            _lastTransport.onConnected -= OnConnectionDiag;
            _lastTransport.onDisconnected -= OnDisconnectionDiag;
        }
        transport.onDataSent += OnDataSentCallback;
        transport.onDataReceived += OnDataReceivedCallback;
        transport.onConnected += OnConnectionDiag;
        transport.onDisconnected += OnDisconnectionDiag;
        _lastTransport = transport;
    }

    private void OnConnectionDiag(Connection conn, bool asServer)
    {
        Debug.Log($"[NetDiag] {_role} connected conn={conn.connectionId} asServer={asServer} t={Time.realtimeSinceStartup:F2}");
    }

    private void OnDisconnectionDiag(Connection conn, DisconnectReason reason, bool asServer)
    {
        Debug.Log($"[NetDiag] {_role} disconnected conn={conn.connectionId} reason={reason} asServer={asServer} t={Time.realtimeSinceStartup:F2}");
    }

    private ulong _dataSent;
    private ulong _dataReceived;

    private void OnDataSentCallback(Connection conn, ByteData data, bool asServer)
    {
        _dataSent += (ulong)data.length;
    }

    private void OnDataReceivedCallback(Connection conn, ByteData data, bool asServer)
    {
        _dataReceived += (ulong)data.length;
    }

    private void LoadArgs()
    {
        if (!TryResolveRole(out _role))
        {
            Application.Quit(-1);
            return;
        }

        if (CommandLineUtils.TryGetArgument("-count", out var countString))
        {
            if (!int.TryParse(countString, out _expectedConnections) || _expectedConnections < 1)
            {
                Debug.LogError($"Could not parse positive -count value '{countString}'");
                Application.Quit(-1);
                return;
            }
        }
        else
        {
#if UNITY_EDITOR
            _expectedConnections = _editorExpectedConnections;
#else
            Debug.LogError("Expected -count argument");
            Application.Quit(-1);
            return;
#endif
        }

        CommandLineUtils.TryGetArgument("-results", out _resultsPath);

        if (CommandLineUtils.TryGetArgument("-connectTimeout", out var connectTimeout)
            && float.TryParse(connectTimeout, out var parsedConnectTimeout))
            _connectionTimeout = parsedConnectTimeout;

        CommandLineUtils.TryGetArgument("-serverHost", out _serverHost);

        if (CommandLineUtils.TryGetArgument("-port", out var port)
            && ushort.TryParse(port, out var parsedPort))
            _port = parsedPort;

        if (CommandLineUtils.TryGetArgument("-latencyMin", out var latencyMin)
            && int.TryParse(latencyMin, out var parsedLatencyMin))
            _minLatencyMs = parsedLatencyMin;

        if (CommandLineUtils.TryGetArgument("-latencyMax", out var latencyMax)
            && int.TryParse(latencyMax, out var parsedLatencyMax))
        {
            _maxLatencyMs = parsedLatencyMax;
            _simulateLatency = parsedLatencyMax > 0;
        }

        if (CommandLineUtils.TryGetArgument("-packetLoss", out var packetLoss)
            && int.TryParse(packetLoss, out var parsedPacketLoss))
            _packetLossChance = parsedPacketLoss;

        _profileScenarios = CommandLineUtils.HasFlag("-profileScenarios");
    }

    private void ConfigureTransport()
    {
        if (_networkManager.transport is not UDPTransport udp)
            return;

        if (_port.HasValue)
            udp.serverPort = _port.Value;

        if (_role == NetworkRole.Client && !string.IsNullOrEmpty(_serverHost))
            udp.address = _serverHost;

        if (_role != NetworkRole.Client)
            udp.maxConnections = Mathf.Max(udp.maxConnections, _expectedConnections + 8);

        udp.networkSimulation = new NetworkSimulation
        {
            includeInBuild = true,
            simulateLatency = _simulateLatency && _maxLatencyMs > 0,
            minLatency = _minLatencyMs,
            maxLatency = _maxLatencyMs,
            simulatePacketLoss = _packetLossChance > 0,
            packetLossChance = Mathf.Max(1, _packetLossChance)
        };
    }

    private bool TryResolveRole(out NetworkRole resolved)
    {
        resolved = default;

        if (CommandLineUtils.TryGetArgument("-role", out var role))
        {
            switch (role)
            {
                case "server": resolved = NetworkRole.Server; return true;
                case "client": resolved = NetworkRole.Client; return true;
                case "host":   resolved = NetworkRole.Host;   return true;
                default:
                    Debug.LogError($"Unknown role '{role}'");
                    return false;
            }
        }

#if UNITY_EDITOR
        resolved = PurrNet.Utils.ApplicationContext.isClone ? NetworkRole.Client : _editorRole;
        return true;
#else
        Debug.LogError("Expected -role argument");
        return false;
#endif
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        Assert.IsTrue(_networkManager.isOffline);
        Assert.AreEqual(_networkManager.clientState, ConnectionState.Disconnected, "Client is not connected");
        Assert.AreEqual(_networkManager.serverState, ConnectionState.Disconnected, "Server is not started");

        if (ctx.isServer)
            _networkManager.StartServer();
        if (ctx.isClient)
            _networkManager.StartClient();

        try
        {
            await UniTaskUtils.WaitWithTimeout(IsConnected, _connectionTimeout, ctx.cancellationToken);
            Debug.Log(
                $"[PredictionTests] {_role} local connection ready " +
                $"(server={_networkManager.isServer}, client={_networkManager.isClient}, players={_networkManager.playerCount}/{_expectedConnections})");
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"local {_role} never connected (server={_networkManager.isServer}, client={_networkManager.isClient})");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _predictionManager && _predictionManager.isSpawned,
                _connectionTimeout,
                ctx.cancellationToken);

            var startTick = _predictionManager.localTick;
            await UniTaskUtils.WaitWithTimeout(
                () => _predictionManager.localTick > startTick + 5,
                _connectionTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"PredictionManager never started ticking (spawned={_predictionManager && _predictionManager.isSpawned}, tick={_predictionManager.localTick})");
        }

        if (!ctx.isServer)
            return ScenarioResult.Ok();

        try
        {
            await UniTaskUtils.WaitWithTimeout(AllConnected, _connectionTimeout, ctx.cancellationToken);
            Debug.Log($"[PredictionTests] {_role} all players connected ({_networkManager.playerCount}/{_expectedConnections})");
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"only {_networkManager.playerCount}/{_expectedConnections} players joined within {_connectionTimeout}s — " +
                "are all client processes/clones running the Bootstrap scene?");
        }

        return ScenarioResult.Ok();
    }

    bool IsConnected()
    {
        bool isServer = _networkManager.isServer;
        bool isClient = _networkManager.isClient;
        switch (_role)
        {
            case NetworkRole.Server or NetworkRole.Host when !isServer:
            case NetworkRole.Client or NetworkRole.Host when !isClient:
                return false;
            default:
                return isServer || isClient;
        }
    }

    bool AllConnected()
    {
        return _networkManager.playerCount >= _expectedConnections;
    }

    private async void RunScenarios()
    {
        bool anyFailed = false;

        try
        {
            var ctx = MakeContext();

            if (_scenarios.Length > 0)
            {
                if (await RunOne(0, ctx))
                {
                    anyFailed = true;
                    if (ctx.isServer)
                        ScenarioSequencer.IssueSequenceComplete();
                    return;
                }
            }

            if (ctx.isServer)
            {
                for (var i = 1; i < _scenarios.Length; i++)
                {
                    if (_networkManager.playerCount < _expectedConnections)
                    {
                        anyFailed = true;
                        AbortRemainingScenarios(i);
                        break;
                    }

                    ScenarioSequencer.IssuePrepare(i);
                    await ScenarioSequencer.WaitForAllReady(ctx, i);

                    var startTick = GetScenarioStartTick();
                    ScenarioSequencer.IssueStart(i, startTick);

                    if (await RunOne(i, ctx, startTick))
                        anyFailed = true;

                    await ScenarioSequencer.WaitForAllAcks(ctx, i);

                    if (i == _scenarios.Length - 1)
                        break;

                    await UniTask.WaitForSeconds(_timeBetweenScenarios);
                }

                ScenarioSequencer.IssueSequenceComplete();
                await ScenarioSequencer.WaitForEndOfRunHandshake(ctx);
            }
            else
            {
                for (var i = 1; i < _scenarios.Length; i++)
                {
                    await ScenarioSequencer.WaitForPrepare(ctx, i);

                    if (ScenarioSequencer.SequenceComplete)
                        break;

                    ScenarioSequencer.AckLocalReady(ctx, i);
                    var startTick = await ScenarioSequencer.WaitForStart(ctx, i);

                    if (ScenarioSequencer.SequenceComplete)
                        break;

                    if (await RunOne(i, ctx, startTick))
                        anyFailed = true;

                    ScenarioSequencer.AckLocalDone(ctx, i);

                    if (i == _scenarios.Length - 1)
                        break;

                    await UniTask.WaitForSeconds(_timeBetweenScenarios);
                }

                try
                {
                    await UniTaskUtils.WaitWithTimeout(
                        () => ScenarioSequencer.SequenceComplete,
                        90f,
                        ctx.cancellationToken);
                }
                catch (TimeoutException e)
                {
                    Debug.LogWarning($"Timed out waiting for scenario sequence cleanup after all local scenarios completed: {e.Message}");
                }

                ScenarioSequencer.AckEndOfRun(ctx);
                await UniTask.NextFrame();
                await UniTask.NextFrame();
            }
        }
        catch (Exception e)
        {
            anyFailed = true;
            Debug.LogException(e);
        }
        finally
        {
            bool anyResultFailed = false;
            for (var i = 0; i < _results.Length; i++)
            {
                if (!_results[i].HasValue || !_results[i].Value.result.success)
                {
                    anyResultFailed = true;
                    break;
                }
            }

            WriteResults();

#if UNITY_EDITOR
            if (anyResultFailed || anyFailed)
                WriteFailedResults();
#else
            Application.Quit(anyResultFailed || anyFailed ? -1 : 0);
#endif
        }
    }

    private void Update()
    {
        _activePerformanceSampler?.SampleFrame(_predictionManager);
    }

    private ulong GetScenarioStartTick()
    {
        var leadTicks = (ulong)Mathf.Max(1, Mathf.CeilToInt(_predictionManager.tickRate * ScenarioStartLeadSeconds));
        return _predictionManager.time.tick + leadTicks;
    }

    private void AbortRemainingScenarios(int fromIndex)
    {
        var message = $"aborted: only {_networkManager.playerCount}/{_expectedConnections} clients still connected";
        Debug.LogError($"[PredictionTests] {message}; skipping scenarios {fromIndex}..{_scenarios.Length - 1}");

        for (var i = fromIndex; i < _scenarios.Length; i++)
        {
            _results[i] = new ScenarioDetails
            {
                name = _scenarios[i].GetType().Name,
                result = ScenarioResult.Fail(message)
            };
        }
    }

    private async UniTask<bool> RunOne(int i, ScenarioContext ctx, ulong scheduledStartTick = 0)
    {
        _dataSent = 0;
        _dataReceived = 0;
        _unexpectedLogs.Clear();
        _activeScenarioIndex = i;

        var scenario = _scenarios[i];
        scenario.PrepareRun(ctx, scheduledStartTick);
        Debug.Log($"[PredictionTests] {_role} starting scenario {i}: {scenario.GetType().Name}");

        long startTick = DateTime.Now.Ticks;
        ScenarioPerformanceDetails performance = default;
        ScenarioPerformanceSampler sampler = null;
        ScenarioResult result;

        if (_profileScenarios)
        {
            sampler = ScenarioPerformanceSampler.StartDefault();
            _activePerformanceSampler = sampler;
        }

        try
        {
            result = await GetResult(scenario, ctx, i);
        }
        finally
        {
            if (sampler != null)
            {
                performance = sampler.Stop(_predictionManager);
                sampler.Dispose();
                if (ReferenceEquals(_activePerformanceSampler, sampler))
                    _activePerformanceSampler = null;
            }

            _activeScenarioIndex = -1;
        }

        result = IncludeUnexpectedLogs(result);
        var elapsedTick = DateTime.Now.Ticks - startTick;
        var elapsedMs = elapsedTick / (double)TimeSpan.TicksPerMillisecond;

        _results[i] = new ScenarioDetails
        {
            name = scenario.GetType().Name,
            result = result,
            durationInMs = elapsedMs,
            dataSent = _dataSent,
            dataReceived = _dataReceived,
            performance = performance
        };

        Debug.Log(
            $"[PredictionTests] {_role} finished scenario {i}: {scenario.GetType().Name} " +
            $"{(result.success ? "PASS" : "FAIL")} ({elapsedMs:F0} ms)");
        if (!result.success)
            Debug.LogWarning($"[PredictionTests] {scenario.GetType().Name} failed: {result.message}");

        return !result.success;
    }

    private void OnLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        if (_activeScenarioIndex < 0)
            return;

        if (type is not (LogType.Error or LogType.Assert or LogType.Exception))
            return;

        if (_unexpectedLogs.Count >= MaxUnexpectedLogsPerScenario)
            return;

        var text = condition ?? string.Empty;
        if (!string.IsNullOrEmpty(stackTrace))
        {
            var lineEnd = stackTrace.IndexOf('\n');
            var firstStackLine = lineEnd >= 0 ? stackTrace[..lineEnd] : stackTrace;
            text += $" at {firstStackLine.Trim()}";
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length > MaxUnexpectedLogLength)
            text = text[..MaxUnexpectedLogLength] + "...";

        _unexpectedLogs.Add($"{type}: {text}");
    }

    private ScenarioResult IncludeUnexpectedLogs(ScenarioResult result)
    {
        if (_unexpectedLogs.Count == 0)
            return result;

        var message = "unexpected logs: " + string.Join(" | ", _unexpectedLogs);

        return result.success
            ? ScenarioResult.Fail(message)
            : ScenarioResult.Fail($"{result.message} | {message}");
    }

    private static async Task<ScenarioResult> GetResult(Scenario scenario, ScenarioContext ctx, int i)
    {
        ScenarioResult details;

        try
        {
            details = await scenario.RunScenario(ctx);
        }
        catch (Exception e)
        {
            details = ScenarioResult.Fail(e.Message);
            Debug.LogError($"Scenario [{i}] `{scenario.name}` failed with: {e}");
        }

        return details;
    }

    private void WriteResults()
    {
        var json = JArray.FromObject(_results).ToString(Formatting.Indented);
        Debug.Log(json);

        if (string.IsNullOrEmpty(_resultsPath))
            return;

        try
        {
            var dir = Path.GetDirectoryName(_resultsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_resultsPath, json);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to write results to '{_resultsPath}': {e}");
        }
    }

    private void WriteFailedResults()
    {
        var failedResults = new JArray();

        for (var i = 0; i < _results.Length; i++)
        {
            if (_results[i].HasValue)
            {
                var details = _results[i].Value;
                if (!details.result.success)
                    failedResults.Add(JObject.FromObject(details));

                continue;
            }

            failedResults.Add(JObject.FromObject(new ScenarioDetails
            {
                name = _scenarios[i].GetType().Name,
                result = ScenarioResult.Fail("Scenario did not run."),
                durationInMs = 0,
                dataSent = 0,
                dataReceived = 0
            }));
        }

        Debug.LogError("Failed test results:\n" + failedResults.ToString(Formatting.Indented));
    }
}

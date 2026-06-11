using System;
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
    [SerializeField] private NetworkManager _networkManager;
    [SerializeField] private PredictionManager _predictionManager;
    [SerializeField] private float _connectionTimeout = 30f;
    [SerializeField] private float _timeBetweenScenarios = 0.1f;

    [Header("Editor overrides (used when -role / -count are absent)")]
    [SerializeField] private NetworkRole _editorRole = NetworkRole.Host;
    [SerializeField] private int _editorExpectedConnections = 2;

    [Header("Network simulation (overridden by -latencyMin / -latencyMax)")]
    [SerializeField] private bool _simulateLatency = true;
    [SerializeField] private int _minLatencyMs = 40;
    [SerializeField] private int _maxLatencyMs = 80;

    private NetworkRole _role;
    private int _expectedConnections;
    private string _resultsPath;
    private string _serverHost;
    private ushort? _port;

    private Scenario[] _scenarios;
    private ScenarioDetails?[] _results;

    private CancellationTokenSource _runCts;

    private void Awake()
    {
        var prefabs = ScriptableObject.CreateInstance<PredictedPrefabs>();
        prefabs.autoGenerate = false;
        prefabs.searchAllIfNoFolder = false;
        _predictionManager.predictedPrefabs = prefabs;

        LoadArgs();

        _scenarios = GetComponentsInChildren<Scenario>();
        _results = new ScenarioDetails?[_scenarios.Length];
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
        }
        transport.onDataSent += OnDataSentCallback;
        transport.onDataReceived += OnDataReceivedCallback;
        _lastTransport = transport;
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

        if (_role != NetworkRole.Client)
        {
            if (CommandLineUtils.TryGetArgument("-count", out var countString))
            {
                if (!int.TryParse(countString, out _expectedConnections))
                {
                    Debug.LogError($"Could not parse -count value '{countString}'");
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
            simulatePacketLoss = false,
            packetLossChance = 1
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
                    anyFailed = true;
            }

            if (ctx.isServer)
            {
                for (var i = 1; i < _scenarios.Length; i++)
                {
                    ScenarioSequencer.IssueStart(i);

                    if (await RunOne(i, ctx))
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
                    await ScenarioSequencer.WaitForStart(ctx, i);

                    if (ScenarioSequencer.SequenceComplete)
                        break;

                    if (await RunOne(i, ctx))
                        anyFailed = true;

                    ScenarioSequencer.AckLocalDone(ctx, i);

                    if (i == _scenarios.Length - 1)
                        break;

                    await UniTask.WaitForSeconds(_timeBetweenScenarios);
                }

                await UniTaskUtils.WaitWithTimeout(
                    () => ScenarioSequencer.SequenceComplete,
                    90f,
                    ctx.cancellationToken);
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

    private async UniTask<bool> RunOne(int i, ScenarioContext ctx)
    {
        _dataSent = 0;
        _dataReceived = 0;

        var scenario = _scenarios[i];
        long startTick = DateTime.Now.Ticks;
        var result = await GetResult(scenario, ctx, i);
        var elapsedTick = DateTime.Now.Ticks - startTick;
        var elapsedMs = elapsedTick / (double)TimeSpan.TicksPerMillisecond;

        _results[i] = new ScenarioDetails
        {
            name = scenario.GetType().Name,
            result = result,
            durationInMs = elapsedMs,
            dataSent = _dataSent,
            dataReceived = _dataReceived
        };

        return !result.success;
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

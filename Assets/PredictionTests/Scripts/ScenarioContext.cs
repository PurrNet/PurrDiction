using System.Threading;
using PurrNet;
using PurrNet.Prediction;

public enum NetworkRole
{
    Server,
    Client,
    Host
}

public struct ScenarioContext
{
    public int scenarioIndex;
    public NetworkRole role;
    public int expectedConnections;
    public NetworkManager networkManager;
    public PredictionManager predictionManager;
    public CancellationToken cancellationToken;

    /// <summary>Simulated packet loss for this run, 0..100. Timing guarantees differ under loss.</summary>
    public int packetLossPercent;
    public int minLatencyMs;
    public int maxLatencyMs;

    public bool hasPacketLoss => packetLossPercent > 0;

    public bool isServer => role is NetworkRole.Server or NetworkRole.Host;
    public bool isClient => role is NetworkRole.Client or NetworkRole.Host;

    public int externalClientCount => role == NetworkRole.Host ? expectedConnections - 1 : expectedConnections;
}

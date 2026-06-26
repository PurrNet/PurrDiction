public struct ScenarioDetails
{
    public string name;
    public ScenarioResult result;
    public double durationInMs;
    public ulong dataSent;
    public ulong dataReceived;
    public ScenarioPerformanceDetails performance;
}

public struct ScenarioPerformanceDetails
{
    public ScenarioMarkerDetails[] markers;
    public ScenarioHistoryDetails history;
    public ScenarioWorldDetails world;
}

public struct ScenarioMarkerDetails
{
    public string name;
    public long elapsedNanoseconds;
    public long sampleBlockCount;
    public double elapsedMilliseconds;
    public double averageNanoseconds;
}

public struct ScenarioHistoryDetails
{
    public long saveCalls;
    public long nonEventSaveCalls;
    public long eventHandlerSaveCalls;
}

public struct ScenarioWorldDetails
{
    public int sampledFrames;
    public int maxSpawnedIdentities;
    public int finalSpawnedIdentities;
    public double averageSpawnedIdentities;
}

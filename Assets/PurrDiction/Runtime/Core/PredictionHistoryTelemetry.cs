namespace PurrNet.Prediction
{
    public static class PredictionHistoryTelemetry
    {
        public static bool enabled { get; private set; }

        public static long saveCalls { get; private set; }

        public static long nonEventSaveCalls { get; private set; }

        public static long eventHandlerSaveCalls { get; private set; }

        public static void Begin()
        {
            saveCalls = 0;
            nonEventSaveCalls = 0;
            eventHandlerSaveCalls = 0;
            enabled = true;
        }

        public static Snapshot End()
        {
            var snapshot = Capture();
            enabled = false;
            return snapshot;
        }

        public static Snapshot Capture()
        {
            return new Snapshot
            {
                saveCalls = saveCalls,
                nonEventSaveCalls = nonEventSaveCalls,
                eventHandlerSaveCalls = eventHandlerSaveCalls
            };
        }

        internal static void RecordSave(bool isEventHandler)
        {
            if (!enabled)
                return;

            saveCalls++;
            if (isEventHandler)
                eventHandlerSaveCalls++;
            else nonEventSaveCalls++;
        }

        public struct Snapshot
        {
            public long saveCalls;
            public long nonEventSaveCalls;
            public long eventHandlerSaveCalls;
        }
    }
}

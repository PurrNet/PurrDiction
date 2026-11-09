#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;

namespace PurrNet.Prediction.Recording
{
    /// <summary>
    /// The browser side of recording (Plugins/WebGL/PurrRecording.jslib). A Web Worker compresses records
    /// (CompressionStream), stores each recording in IndexedDB as it is written, and uploads finished ones, so the
    /// main thread only encodes and copies bytes. Calls take pointers and numbers, so they make no C# garbage.
    /// </summary>
    static class WebRecording
    {
        public const int StatBytes = 0, StatCompressMs = 1, StatDeflate = 3;

        [DllImport("__Internal")]
        static extern int PurrRec_Open(string name, double maxLocalBytes);

        [DllImport("__Internal")]
        static extern unsafe void PurrRec_Append(int handle, int kind, int compress, byte* data, int length);

        [DllImport("__Internal")]
        static extern void PurrRec_Close(int handle);

        [DllImport("__Internal")]
        static extern double PurrRec_Stat(int handle, int which);

        [DllImport("__Internal")]
        static extern void PurrRec_Uploads(string endpoint, string projectKey, double maxLocalBytes);

        [DllImport("__Internal")]
        static extern void PurrRec_SetIdle(int idle);

        static bool _warned;

        /// <summary>A handle for a new recording, or 0 when this browser cannot run the worker.</summary>
        public static int Open(string name, long maxLocalBytes)
        {
            int handle = PurrRec_Open(name, maxLocalBytes);
            if (handle == 0 && !_warned)
            {
                _warned = true;
                UnityEngine.Debug.LogWarning("[PredictionRecorder] This browser cannot run the recording worker; matches are not recorded.");
            }
            return handle;
        }

        /// <summary>Hands one record to the worker (kind -1: raw bytes, no framing). The bytes are copied before this returns.</summary>
        public static unsafe void Append(int handle, int kind, bool compress, byte[] data, int length)
        {
            fixed (byte* p = data)
                PurrRec_Append(handle, kind, compress ? 1 : 0, p, length);
        }

        public static void Close(int handle) => PurrRec_Close(handle);

        public static double Stat(int handle, int which) => handle == 0 ? 0 : PurrRec_Stat(handle, which);

        public static void Uploads(string endpoint, string projectKey, long maxLocalBytes) =>
            PurrRec_Uploads(endpoint, projectKey, maxLocalBytes);

        public static void SetIdle(bool idle) => PurrRec_SetIdle(idle ? 1 : 0);
    }
}
#endif

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Unity.Profiling;
using UnityEngine;

namespace PurrNet.Prediction.Recording
{
    public enum RecordKind : byte
    {
        Header = 1,
        Type = 2,
        Stream = 3,
        Entity = 4,
        Despawn = 5,
        Chunk = 6,
        Colliders = 7,
        Player = 8,
        Footer = 9,
        Mesh = 10,
        Batch = 11
    }

    /// <summary>A record payload built on the writer thread from pooled data.</summary>
    public interface IRecordProducer
    {
        /// <summary>The payload; read before <see cref="Release"/>.</summary>
        RecordingBuffer Produce();

        /// <summary>Called on the writer thread once the payload is written (or dropped).</summary>
        void Release();
    }

    /// <summary>
    /// Writes the .purrrec container. Records are framed on the caller's thread; compression and disk
    /// writes happen on a background thread so a host does not hitch when a chunk is flushed.
    /// </summary>
    public sealed class RecordingFile : IDisposable
    {
        struct PendingRecord
        {
            public RecordKind kind;
            public byte[] payload;
            public IRecordProducer producer;
            public bool compress;
        }

        // A plain queue under a lock: it reuses its array, where a BlockingCollection allocates as records pass.
        readonly Queue<PendingRecord> _queue = new Queue<PendingRecord>(64);
        bool _completed;
        readonly Thread _thread;
        readonly FileStream _stream;
        long _bytesWritten;
        long _rawBytes;
        volatile Exception _error;
        // Writer thread only: compressed output, reused between records.
        readonly BufferStream _compressed = new BufferStream(new RecordingBuffer(ByteShelf.shared, 1 << 16));
        // Lets the native deflate callback find the output buffer without allocating per record.
        GCHandle _compressedHandle;

        volatile bool _busy;
        // Writer thread CPU spent producing (encoding) and compressing records, in Stopwatch ticks.
        long _produceTicks, _compressTicks, _maxRecordTicks;

        /// <summary>Writer-thread milliseconds spent encoding chunks and compressing records, and the longest record.</summary>
        public double produceMs => Interlocked.Read(ref _produceTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public double compressMs => Interlocked.Read(ref _compressTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public double maxRecordMs => Interlocked.Read(ref _maxRecordTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>True while the writer thread encodes or compresses a record (it allocates while doing so).</summary>
        public bool busy
        {
            get
            {
                lock (_queue)
                    return _busy || _queue.Count > 0;
            }
        }

        public string path { get; }
        /// <summary>False once a record had to be compressed with DeflateStream (it allocates per record).</summary>
        public bool nativeDeflate { get; private set; } = true;
        public long bytesWritten => Interlocked.Read(ref _bytesWritten);
        public long rawBytes => Interlocked.Read(ref _rawBytes);

        public RecordingFile(string path)
        {
            this.path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            var magic = Encoding.ASCII.GetBytes("PURRREC\0");
            _stream.Write(magic, 0, magic.Length);
            _stream.WriteByte(1); // version u16 LE
            _stream.WriteByte(0);
            _bytesWritten = 10;
            _compressedHandle = GCHandle.Alloc(_compressed.buffer);
            _thread = new Thread(WriteLoop) { IsBackground = true, Name = "PurrDiction recording writer" };
            _thread.Start();
        }

        public void WriteJson(RecordKind kind, string json, bool compress = false)
        {
            Enqueue(kind, Encoding.UTF8.GetBytes(json), compress);
        }

        /// <summary>Sizes the compressed-output buffer before records are queued, so compressing does not grow it.</summary>
        public void ReserveOutput(int bytes) => _compressed.buffer.Reserve(bytes);

        public void Enqueue(RecordKind kind, byte[] payload, bool compress)
        {
            if (_error != null)
                return;
            Interlocked.Add(ref _rawBytes, payload.Length);
            Add(new PendingRecord { kind = kind, payload = payload, compress = compress });
        }

        /// <summary>
        /// Queues a record whose payload is produced on the writer thread, in order with the other records. The
        /// producer is released once its bytes are written, so it and its buffers can be pooled.
        /// </summary>
        public void Enqueue(RecordKind kind, IRecordProducer producer, bool compress)
        {
            if (_error != null)
            {
                producer.Release();
                return;
            }
            Add(new PendingRecord { kind = kind, producer = producer, compress = compress });
        }

        static readonly ProfilerMarker EncodeMarker = new ProfilerMarker("PredictionRecorder.Encode");
        static readonly ProfilerMarker CompressMarker = new ProfilerMarker("PredictionRecorder.Compress");
        static readonly ProfilerMarker WriteMarker = new ProfilerMarker("PredictionRecorder.Write");

        void WriteLoop()
        {
            // Visible in the Unity profiler as its own thread.
            UnityEngine.Profiling.Profiler.BeginThreadProfiling("PurrDiction", "Recording writer");
            try
            {
                WriteRecords();
            }
            finally
            {
                UnityEngine.Profiling.Profiler.EndThreadProfiling();
            }
        }

        void Add(in PendingRecord record)
        {
            lock (_queue)
            {
                _queue.Enqueue(record);
                Monitor.Pulse(_queue);
            }
        }

        /// <summary>Waits for the next record; false once the file is closing and every record is written.</summary>
        bool Take(out PendingRecord record)
        {
            lock (_queue)
            {
                while (_queue.Count == 0)
                {
                    if (_completed)
                    {
                        record = default;
                        return false;
                    }
                    Monitor.Wait(_queue);
                }
                record = _queue.Dequeue();
                // Under the lock, so a caller that sees the writer idle knows it stays idle until it queues a record.
                _busy = true;
                return true;
            }
        }

        void WriteRecords()
        {
            var header = new RecordingBuffer(16);
            while (Take(out var record))
            {
                if (_error != null)
                {
                    record.producer?.Release();
                    continue;
                }
                _busy = true;
                try
                {
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    byte[] payload = record.payload;
                    int length = payload?.Length ?? 0;
                    if (record.producer != null)
                    {
                        using var _ = EncodeMarker.Auto();
                        var produced = record.producer.Produce();
                        payload = produced.data;
                        length = produced.length;
                        Interlocked.Add(ref _rawBytes, length);
                    }
                    long encoded = System.Diagnostics.Stopwatch.GetTimestamp();
                    Interlocked.Add(ref _produceTicks, encoded - started);
                    byte kind = (byte)record.kind;
                    if (record.compress)
                    {
                        using var _ = CompressMarker.Auto();
                        _compressed.buffer.Clear();
                        if (!NativeDeflate.TryCompress(payload, length, GCHandle.ToIntPtr(_compressedHandle)))
                        {
                            nativeDeflate = false;
                            _compressed.buffer.Clear();
                            using (var deflate = new DeflateStream(_compressed, System.IO.Compression.CompressionLevel.Optimal, true))
                                deflate.Write(payload, 0, length);
                        }
                        payload = _compressed.buffer.data;
                        length = _compressed.buffer.length;
                        kind |= 0x80;
                    }
                    long compressed = System.Diagnostics.Stopwatch.GetTimestamp();
                    Interlocked.Add(ref _compressTicks, compressed - encoded);
                    if (compressed - started > Interlocked.Read(ref _maxRecordTicks))
                        Interlocked.Exchange(ref _maxRecordTicks, compressed - started);

                    using var write = WriteMarker.Auto();
                    header.Clear();
                    header.WriteByte(kind);
                    header.WriteVarUInt((ulong)length);
                    _stream.Write(header.data, 0, header.length);
                    _stream.Write(payload, 0, length);
                    Interlocked.Add(ref _bytesWritten, header.length + length);
                }
                catch (Exception e)
                {
                    _error = e;
                }
                finally
                {
                    record.producer?.Release();
                    _busy = false;
                }
            }
            _stream.Flush();
        }

        public void Dispose()
        {
            lock (_queue)
            {
                _completed = true;
                Monitor.PulseAll(_queue);
            }
            _thread.Join();
            if (_compressedHandle.IsAllocated)
                _compressedHandle.Free();
            _stream.Dispose();
            if (_error != null)
                Debug.LogError($"[PredictionRecorder] Writing {path} failed: {_error}");
        }
    }

    /// <summary>A write-only stream into a <see cref="RecordingBuffer"/>, so compressed output reuses shelf arrays.</summary>
    sealed class BufferStream : Stream
    {
        public readonly RecordingBuffer buffer;

        public BufferStream(RecordingBuffer buffer) => this.buffer = buffer;

        public override void Write(byte[] source, int offset, int count) => buffer.WriteBytes(source, offset, count);
        public override void WriteByte(byte value) => buffer.WriteByte(value);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => buffer.length;
        public override long Position
        {
            get => buffer.length;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] destination, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Raw deflate through the zlib helper Unity's runtimes ship (the one <see cref="DeflateStream"/> itself calls, with
    /// the same default level, so the bytes are identical). DeflateStream allocates handles and buffers for every
    /// record; this reuses one callback and one handle, so compressing makes no garbage. Falls back to DeflateStream
    /// where the helper is missing.
    /// </summary>
    static class NativeDeflate
    {
        delegate int Feeder(IntPtr buffer, int length, IntPtr state);

        [DllImport("MonoPosixHelper")]
        static extern IntPtr CreateZStream(int compress, byte gzip, IntPtr feeder, IntPtr state);

        [DllImport("MonoPosixHelper")]
        static extern int CloseZStream(IntPtr stream);

        [DllImport("MonoPosixHelper")]
        static extern int WriteZStream(IntPtr stream, IntPtr buffer, int length);

        static readonly Feeder FeederDelegate = Feed;
        static IntPtr _feeder;
        // 0: not tried yet, 1: works, -1: unavailable.
        static volatile int _state;

        public static bool available => _state >= 0;

        [AOT.MonoPInvokeCallback(typeof(Feeder))]
        static unsafe int Feed(IntPtr buffer, int length, IntPtr state)
        {
            try
            {
                var output = (RecordingBuffer)GCHandle.FromIntPtr(state).Target;
                output.WriteBytes((byte*)buffer, length);
                return length;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>Appends the raw deflate of <paramref name="payload"/> to the buffer behind <paramref name="output"/>.</summary>
        public static unsafe bool TryCompress(byte[] payload, int length, IntPtr output)
        {
            if (_state < 0)
                return false;
            try
            {
                if (_feeder == IntPtr.Zero)
                    _feeder = Marshal.GetFunctionPointerForDelegate(FeederDelegate);
                var stream = CreateZStream(1, 0, _feeder, output);
                if (stream == IntPtr.Zero)
                {
                    _state = -1;
                    return false;
                }
                int written = 0;
                if (length > 0)
                {
                    fixed (byte* p = payload)
                        written = WriteZStream(stream, (IntPtr)p, length);
                }
                int closed = CloseZStream(stream);
                if (written < 0 || closed < 0)
                    return false;
                _state = 1;
                return true;
            }
            catch (Exception)
            {
                // DllNotFoundException, EntryPointNotFoundException: no helper on this platform.
                _state = -1;
                return false;
            }
        }
    }
}

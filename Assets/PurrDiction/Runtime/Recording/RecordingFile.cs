using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
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

        readonly BlockingCollection<PendingRecord> _queue = new BlockingCollection<PendingRecord>();
        readonly Thread _thread;
        readonly FileStream _stream;
        long _bytesWritten;
        long _rawBytes;
        volatile Exception _error;
        // Writer thread only: compressed output, reused between records.
        readonly MemoryStream _compressed = new MemoryStream(1 << 16);

        volatile bool _busy;
        // Writer thread CPU spent producing (encoding) and compressing records, in Stopwatch ticks.
        long _produceTicks, _compressTicks, _maxRecordTicks;

        /// <summary>Writer-thread milliseconds spent encoding chunks and compressing records, and the longest record.</summary>
        public double produceMs => Interlocked.Read(ref _produceTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public double compressMs => Interlocked.Read(ref _compressTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public double maxRecordMs => Interlocked.Read(ref _maxRecordTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>True while the writer thread encodes or compresses a record (it allocates while doing so).</summary>
        public bool busy => _busy || _queue.Count > 0;

        public string path { get; }
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
            _thread = new Thread(WriteLoop) { IsBackground = true, Name = "PurrDiction recording writer" };
            _thread.Start();
        }

        public void WriteJson(RecordKind kind, string json, bool compress = false)
        {
            Enqueue(kind, Encoding.UTF8.GetBytes(json), compress);
        }

        public void Enqueue(RecordKind kind, byte[] payload, bool compress)
        {
            if (_error != null)
                return;
            Interlocked.Add(ref _rawBytes, payload.Length);
            _queue.Add(new PendingRecord { kind = kind, payload = payload, compress = compress });
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
            _queue.Add(new PendingRecord { kind = kind, producer = producer, compress = compress });
        }

        void WriteLoop()
        {
            var header = new RecordingBuffer(16);
            foreach (var record in _queue.GetConsumingEnumerable())
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
                        _compressed.SetLength(0);
                        using (var deflate = new DeflateStream(_compressed, System.IO.Compression.CompressionLevel.Optimal, true))
                            deflate.Write(payload, 0, length);
                        payload = _compressed.GetBuffer();
                        length = (int)_compressed.Length;
                        kind |= 0x80;
                    }
                    long compressed = System.Diagnostics.Stopwatch.GetTimestamp();
                    Interlocked.Add(ref _compressTicks, compressed - encoded);
                    if (compressed - started > Interlocked.Read(ref _maxRecordTicks))
                        Interlocked.Exchange(ref _maxRecordTicks, compressed - started);

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
            _queue.CompleteAdding();
            _thread.Join();
            _stream.Dispose();
            _queue.Dispose();
            if (_error != null)
                Debug.LogError($"[PredictionRecorder] Writing {path} failed: {_error}");
        }
    }

}

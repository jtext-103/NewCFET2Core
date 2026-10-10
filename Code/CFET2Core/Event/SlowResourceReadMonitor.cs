using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Log;

namespace Jtext103.CFET2.Core.Event
{
    // One monitor for all reads, not a worker/thread per getter. Logging runs outside device locks.
    internal sealed class SlowResourceReadMonitor : IDisposable
    {
        internal static readonly SlowResourceReadMonitor Shared = new SlowResourceReadMonitor(
            () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency,
            message => Cfet2LogManager.GetLogger("SlowResourceRead").Error(message), true);

        private sealed class Read : IDisposable
        {
            internal SlowResourceReadMonitor Owner;
            internal long Id;
            internal string Path, Stage;
            internal double Start, Threshold;
            internal bool Checked, Reported;
            public void Dispose() => Interlocked.Exchange(ref Owner, null)?.End(this);
        }
        private sealed class Rate { internal double Last; internal int Suppressed; }
        private readonly object sync = new object();
        private readonly Dictionary<long, Read> reads = new Dictionary<long, Read>();
        private readonly Dictionary<string, Rate> rates = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> messages = new Queue<string>();
        private readonly Func<double> now;
        private readonly Action<string> log;
        private readonly Timer timer;
        private long nextId;
        private int ticking;
        private bool disposed;
        internal SlowResourceReadMonitor(Func<double> now, Action<string> log, bool automatic = false)
        {
            this.now = now;
            this.log = log;
            if (automatic) timer = new Timer(_ => Tick(), null, 25, 25);
        }
        internal IDisposable Begin(string path, string stage, double threshold)
        {
            lock (sync)
            {
                var read = new Read { Owner = this, Id = ++nextId, Path = path, Stage = stage,
                    Start = now(), Threshold = threshold > 0 && !double.IsInfinity(threshold) ? threshold : 100 };
                if (!disposed) reads.Add(read.Id, read);
                return read;
            }
        }
        internal static T Measure<T>(string path, string stage, Func<T> getter)
        {
            using (Shared.Begin(path, stage, GlobalConfig.ResourceSlowGetThresholdMs)) return getter();
        }
        private void Check(Read read, double time)
        {
            if (read.Checked || time - read.Start < read.Threshold) return;
            read.Checked = true;
            if (rates.TryGetValue(read.Path, out var rate) && time - rate.Last < 1000)
            { rate.Suppressed++; return; }
            var suppressed = rate?.Suppressed ?? 0;
            rates[read.Path] = new Rate { Last = time };
            read.Reported = true;
            messages.Enqueue(Format(read, time, "slow", suppressed));
        }
        private static string Format(Read read, double time, string state, int suppressed = 0) =>
            $"Resource Get {state}: path={read.Path}, stage={read.Stage}, read={read.Id}, " +
            $"elapsedMs={time - read.Start:F1}, thresholdMs={read.Threshold:F1}, suppressed={suppressed}";
        private void End(Read read)
        {
            lock (sync)
            {
                if (!reads.Remove(read.Id)) return;
                var time = now();
                Check(read, time);
                if (read.Reported) messages.Enqueue(Format(read, time, "completed"));
            }
        }
        internal int ActiveCount { get { lock (sync) return reads.Count; } }
        internal void Tick()
        {
            if (Interlocked.Exchange(ref ticking, 1) != 0) return;
            try
            {
                string[] output;
                lock (sync)
                {
                    if (disposed) return;
                    var time = now();
                    foreach (var read in reads.Values) Check(read, time);
                    foreach (var item in rates.Where(p => time - p.Value.Last >= 1000).ToArray())
                    {
                        if (item.Value.Suppressed > 0)
                            messages.Enqueue($"Resource Get slow summary: path={item.Key}, suppressed={item.Value.Suppressed}");
                        rates.Remove(item.Key);
                    }
                    output = messages.ToArray();
                    messages.Clear();
                }
                foreach (var message in output)
                    try { log(message); } catch { /* Diagnostics must not break resource observation. */ }
            }
            finally { Volatile.Write(ref ticking, 0); }
        }
        public void Dispose()
        {
            lock (sync) { disposed = true; reads.Clear(); rates.Clear(); messages.Clear(); }
            timer?.Dispose();
        }
    }
}

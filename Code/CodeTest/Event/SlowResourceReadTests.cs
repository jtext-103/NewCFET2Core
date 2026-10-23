using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event;

[TestClass]
[DoNotParallelize]
public class SlowResourceReadTests
{
    [TestMethod]
    public void ThresholdIsInclusiveAndCompletionDoesNotAlertTwice()
    {
        double time = 0;
        var logs = new List<string>();
        using var monitor = new SlowResourceReadMonitor(() => time, logs.Add);
        var read = monitor.Begin("/device/a", "initial", 100);
        time = 99; monitor.Tick(); Assert.AreEqual(0, logs.Count);
        time = 100; monitor.Tick(); Assert.AreEqual(1, logs.Count);
        StringAssert.Contains(logs[0], "stage=initial");
        time = 130; read.Dispose(); read.Dispose(); monitor.Tick();
        Assert.AreEqual(2, logs.Count);
        StringAssert.Contains(logs[1], "completed");
        Assert.AreEqual(0, monitor.ActiveCount);
    }

    [TestMethod]
    public void CompletionBeforeTimerStillDetectsSlowReadAndDoesNotAffectNextRead()
    {
        double time = 0;
        var logs = new List<string>();
        using var monitor = new SlowResourceReadMonitor(() => time, logs.Add);
        using (monitor.Begin("/a", "poll", 100)) time = 150;
        using (monitor.Begin("/a", "poll", 100)) { monitor.Tick(); time = 160; }
        monitor.Tick();
        Assert.AreEqual(1, logs.Count(s => s.Contains("Get slow:")));
        Assert.AreEqual(1, logs.Count(s => s.Contains("Get completed:")));
        Assert.AreEqual(0, monitor.ActiveCount);
    }

    [TestMethod]
    public void RateLimitIsPerResourceAndSummarizesSuppressedReads()
    {
        double time = 0;
        var logs = new List<string>();
        using var monitor = new SlowResourceReadMonitor(() => time, logs.Add);
        using (monitor.Begin("/a", "poll", 10)) time += 20;
        using (monitor.Begin("/a", "poll", 10)) time += 20;
        using (monitor.Begin("/b", "poll", 10)) time += 20;
        monitor.Tick();
        Assert.AreEqual(2, logs.Count(s => s.Contains("Get slow:")));
        time = 1100; monitor.Tick();
        Assert.IsTrue(logs.Any(s => s.Contains("summary: path=/a, suppressed=1")));
        using (monitor.Begin("/a", "poll", 10)) time += 20;
        monitor.Tick();
        Assert.AreEqual(3, logs.Count(s => s.Contains("Get slow:")));
    }

    [TestMethod]
    public void LoggingOutsideMonitorLockAllowsOtherReadsAndLoggerFailureIsContained()
    {
        double time = 0;
        SlowResourceReadMonitor? monitor = null;
        var loggedOutsideLock = false;
        monitor = new SlowResourceReadMonitor(() => time, _ =>
        {
            var task = Task.Run(() => { using var read = monitor!.Begin("/other", "poll", 100); });
            loggedOutsideLock = task.Wait(2000);
            throw new InvalidOperationException("logger failure");
        });
        using (monitor)
        {
            using (monitor.Begin("/a", "poll", 10)) time = 20;
            monitor.Tick();
            Assert.IsTrue(loggedOutsideLock);
            Assert.AreEqual(0, monitor.ActiveCount);
        }
    }

    [TestMethod]
    public async Task CompletionAndTimerRacesDoNotDuplicateOrLeakReadRecords()
    {
        double time = 0;
        var logs = new List<string>();
        using var monitor = new SlowResourceReadMonitor(() => time, logs.Add);
        for (int i = 0; i < 30; i++)
        {
            var read = monitor.Begin("/race" + i, "poll", 100);
            time += 100;
            await Task.WhenAll(Task.Run(read.Dispose), Task.Run(monitor.Tick));
            monitor.Tick();
            Assert.AreEqual(0, monitor.ActiveCount);
        }
        Assert.AreEqual(30, logs.Count(s => s.Contains("Get slow:")));
        Assert.AreEqual(30, logs.Count(s => s.Contains("Get completed:")));
    }

    [TestMethod]
    public async Task AutomaticMonitorReportsWhileGetterHasNotReturned()
    {
        var alarm = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var monitor = new SlowResourceReadMonitor(
            () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
            s => alarm.TrySetResult(s), true);
        var worker = Task.Run(() => { using var read = monitor.Begin("/blocked", "initial", 30); entered.Set(); gate.Wait(); });
        try
        {
            Assert.IsTrue(entered.Wait(2000));
            var message = await alarm.Task.WaitAsync(TimeSpan.FromSeconds(3));
            StringAssert.Contains(message, "/blocked");
            Assert.IsFalse(worker.IsCompleted);
        }
        finally { gate.Set(); await worker; }
        Assert.AreEqual(0, monitor.ActiveCount);
    }

    [TestMethod]
    public void DisposeAndExceptionalReadReleaseRecords()
    {
        double time = 0;
        var logs = new List<string>();
        using var monitor = new SlowResourceReadMonitor(() => time, logs.Add);
        try { using var read = monitor.Begin("/a", "initial", 10); time = 20; throw new System.Exception(); }
        catch { }
        Assert.AreEqual(0, monitor.ActiveCount);
        var late = monitor.Begin("/b", "poll", 10);
        monitor.Dispose(); late.Dispose(); monitor.Tick();
        Assert.AreEqual(0, monitor.ActiveCount);
    }

    [TestMethod]
    public void SlowThresholdConfigRoundTripsAndInvalidValuesDefault()
    {
        var original = GlobalConfig.Get();
        try
        {
            foreach (var value in new[] { "0", "-1", "bad", "Infinity", "" })
            {
                var config = new Dictionary<string, string>(original) { ["ResourceSlowGetThresholdMs"] = value };
                GlobalConfig.Set(config);
                Assert.AreEqual(100, GlobalConfig.ResourceSlowGetThresholdMs);
            }
            var valid = new Dictionary<string, string>(original) { ["ResourceSlowGetThresholdMs"] = "250" };
            GlobalConfig.Set(valid);
            Assert.AreEqual("250", GlobalConfig.Get()["ResourceSlowGetThresholdMs"]);
            valid.Remove("ResourceSlowGetThresholdMs"); GlobalConfig.Set(valid);
            Assert.AreEqual(100, GlobalConfig.ResourceSlowGetThresholdMs);
        }
        finally { GlobalConfig.Set(original); }
    }

    [TestMethod]
    public void SlowThresholdFilePopulateUsesSameValidationAndDefaults()
    {
        var original = GlobalConfig.Get();
        var path = System.IO.Path.GetTempFileName();
        try
        {
            foreach (var value in new string?[] { "250", "0", "-1", "bad", "Infinity", "", null })
            {
                var config = new Dictionary<string, string>(original);
                if (value == null) config.Remove("ResourceSlowGetThresholdMs"); else config["ResourceSlowGetThresholdMs"] = value;
                var json = Newtonsoft.Json.Linq.JObject.FromObject(config);
                json["ResourceMinDelta"] = GlobalConfig.ResourceMinDelta;
                json["EventPublishTransports"] = Newtonsoft.Json.Linq.JArray.Parse(GlobalConfig.Get()["EventPublishTransports"]);
                json["WebSocketPortOffset"] = GlobalConfig.WebSocketPortOffset;
                foreach (var key in config.Keys.Where(k => (k.StartsWith("Mqtt") && k.EndsWith("Seconds")) || k == "MqttBrokerPort"))
                    json[key] = int.Parse(config[key], System.Globalization.CultureInfo.InvariantCulture);
                System.IO.File.WriteAllText(path, json.ToString());
                GlobalConfig.Populate(path);
                Assert.AreEqual(value == "250" ? 250 : 100, GlobalConfig.ResourceSlowGetThresholdMs);
            }
        }
        finally { GlobalConfig.Set(original); System.IO.File.Delete(path); }
    }
}

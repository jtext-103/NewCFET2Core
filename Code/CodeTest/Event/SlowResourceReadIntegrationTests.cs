using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Log;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event;

[TestClass, DoNotParallelize]
public partial class SlowResourceReadIntegrationTests : CFET2Host
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task ActualInitialAndPollingReadsReportBeforeReturningAndPreserveResults(bool exact, bool polling)
    {
        var providerField = typeof(Cfet2LogManager).GetField("provider", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldProvider = providerField.GetValue(null);
        var oldThreshold = GlobalConfig.ResourceSlowGetThresholdMs;
        var oldInterval = GlobalConfig.ResourceObservationIntervalMs;
        var log = new Capture();
        var device = new BlockingDevice { BlockRead = polling ? 2 : 1 };
        IDisposable? subscription = null;
        Task<IDisposable>? creation = null;
        try
        {
            GlobalConfig.ResourceSlowGetThresholdMs = 30;
            GlobalConfig.ResourceObservationIntervalMs = 20;
            providerField.SetValue(null, log);
            HubMaster.InjectHubToModule(this);
            MyHub.TryAddThing(device, "/", "slow" + Guid.NewGuid().ToString("N"));
            var path = device.Path + "/Value";
            var received = new ConcurrentQueue<EventArg>();
            creation = Task.Run<IDisposable>(() => exact
                ? MyHub.SubscribeResource(path, received.Enqueue)
                : MyHub.SubscribeResources(new EventFilter(path + ".*", "changed"), received.Enqueue));
            Assert.IsTrue(device.Entered.Wait(2000));
            Assert.IsTrue(SpinWait.SpinUntil(() => log.Errors.Any(s => s.Contains("Get slow:") && s.Contains(path)), 3000));
            Assert.IsFalse(device.Release.IsSet);
            Assert.IsTrue(log.Errors.Any(s => s.Contains(polling ? "stage=poll" : "stage=initial")));
            device.ValueNumber = 5; device.Release.Set();
            subscription = await creation.WaitAsync(TimeSpan.FromSeconds(3));
            if (polling) Assert.IsTrue(SpinWait.SpinUntil(() => received.Any(e => Equals(e.Sample.ObjectVal, 5)), 3000));
            else
            {
                Assert.AreEqual(0, received.Count, "Slow successful initial value remains a baseline.");
                device.ValueNumber = 6;
                Assert.IsTrue(SpinWait.SpinUntil(() => received.Any(e => Equals(e.Sample.ObjectVal, 6)), 3000));
            }
            Assert.IsTrue(SpinWait.SpinUntil(() => log.Errors.Any(s => s.Contains("Get completed:") && s.Contains(path)), 3000));
            subscription.Dispose(); subscription = null;
            Assert.IsTrue(SpinWait.SpinUntil(() => SlowResourceReadMonitor.Shared.ActiveCount == 0, 2000));
        }
        finally
        {
            device.Release.Set();
            if (creation != null && subscription == null && !creation.IsFaulted) { try { (await creation).Dispose(); } catch { } }
            subscription?.Dispose();
            Hub.KillMaster();
            providerField.SetValue(null, oldProvider);
            GlobalConfig.ResourceSlowGetThresholdMs = oldThreshold;
            GlobalConfig.ResourceObservationIntervalMs = oldInterval;
        }
    }

    public sealed class BlockingDevice : Thing
    {
        public int BlockRead, Reads;
        public volatile int ValueNumber = 1;
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        [Cfet2Status] public int Value
        {
            get
            {
                if (Interlocked.Increment(ref Reads) == BlockRead)
                { Entered.Set(); if (!Release.Wait(10000)) throw new TimeoutException("Test gate"); }
                return ValueNumber;
            }
        }
    }
    private sealed class Capture : ICfet2LogProvider, ICfet2Logger
    {
        public readonly ConcurrentQueue<string> Errors = new();
        public ICfet2Logger GetLogger(string name) => this;
        public void StartLogCleanup() { }
        public void StopLogCleanup() { }
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => Errors.Enqueue(message);
        public void Fatal(string message) { }
    }
}

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
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event;

public partial class SlowResourceReadIntegrationTests
{
    private sealed class MonitorScope : IDisposable
    {
        private readonly FieldInfo field = typeof(Cfet2LogManager).GetField("provider", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? original;
        private readonly int threshold = GlobalConfig.ResourceSlowGetThresholdMs;
        private readonly int interval = GlobalConfig.ResourceObservationIntervalMs;
        public readonly Capture Log = new();
        public MonitorScope()
        {
            try { Hub.KillMaster(); } catch (NullReferenceException) { }
            original = field.GetValue(null); field.SetValue(null, Log);
            GlobalConfig.ResourceSlowGetThresholdMs = 30;
            GlobalConfig.ResourceObservationIntervalMs = 20;
        }
        public void Dispose()
        {
            Hub.KillMaster(); field.SetValue(null, original);
            GlobalConfig.ResourceSlowGetThresholdMs = threshold;
            GlobalConfig.ResourceObservationIntervalMs = interval;
        }
    }

    private static void Until(Func<bool> predicate) => Assert.IsTrue(SpinWait.SpinUntil(predicate, 4000));
    private int Observations => (int)typeof(Hub).GetProperty("ObservedResourceCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(MyHub)!;

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public async Task SlowThrowOrInvalidSamplePreservesInitialAndPollingFailurePolicy(int stage, bool invalidSample)
    {
        using var scope = new MonitorScope(); HubMaster.InjectHubToModule(this);
        var device = new FailureDevice { BlockRead = stage == 2 ? 2 : 1, FailureFrom = stage == 2 ? 2 : 1, InvalidSample = invalidSample };
        MyHub.TryAddThing(device, "/", "failure" + Guid.NewGuid().ToString("N"));
        var path = device.Path + "/Value";
        var events = new ConcurrentQueue<EventArg>();
        IDisposable? subscription = null;
        var creation = Task.Run<IDisposable>(() => stage == 1
            ? MyHub.SubscribeResource(path, events.Enqueue)
            : MyHub.SubscribeResources(new EventFilter(path + ".*", "changed"), events.Enqueue));
        try
        {
            Assert.IsTrue(device.Entered.Wait(2000));
            Until(() => scope.Log.Errors.Any(x => x.Contains("Get slow:") && x.Contains(path)));
            device.Release.Set();
            if (stage == 1)
            {
                await Assert.ThrowsExactlyAsync<Jtext103.CFET2.Core.Exception.BadResourceRequestException>(async () => await creation);
                Assert.AreEqual(0, Observations);
            }
            else
            {
                subscription = await creation.WaitAsync(TimeSpan.FromSeconds(3));
                var group = (ResourceGroupSubscription)subscription;
                if (stage == 0)
                {
                    Assert.AreEqual(ResourceObservationStatus.Failed, group.ObservationResults.Single().Status);
                    Assert.AreEqual(0, Observations);
                    await Task.Delay(100); Assert.AreEqual(1, device.Reads, "Failed initial read must not retry.");
                }
                else
                {
                    Until(() => device.Reads >= 4);
                    Assert.AreEqual(1, Observations); Assert.AreEqual(0, events.Count);
                    device.ValueNumber = 8; device.FailureFrom = int.MaxValue;
                    Until(() => events.Count == 1); Assert.AreEqual(8, events.Single().Sample.GetVal<int>());
                }
            }
            Until(() => scope.Log.Errors.Any(x => x.Contains("Get completed:") && x.Contains(path)));
            subscription?.Dispose(); subscription = null;
            Until(() => SlowResourceReadMonitor.Shared.ActiveCount == 0);
        }
        finally
        {
            device.Release.Set();
            try { (await creation).Dispose(); } catch { }
            subscription?.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BlockedPollingReadKeepsOneMonitorUntilLateReturnAfterUnsubscribeOrShutdown(bool shutdown)
    {
        using var scope = new MonitorScope(); HubMaster.InjectHubToModule(this);
        var device = new BlockingDevice { BlockRead = 2 };
        MyHub.TryAddThing(device, "/", "closing" + Guid.NewGuid().ToString("N"));
        var events = new ConcurrentQueue<EventArg>();
        using var subscription = MyHub.SubscribeResources(new EventFilter(device.Path + "/.*", "changed"), events.Enqueue);
        try
        {
            Assert.IsTrue(device.Entered.Wait(2000));
            Until(() => scope.Log.Errors.Any(x => x.Contains("Get slow:")));
            await Task.Run(() => { if (shutdown) MyHub.EventHub.Dispose(); else subscription.Dispose(); }).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, Observations);
            Assert.AreEqual(1, SlowResourceReadMonitor.Shared.ActiveCount, "The physical getter is still running.");
            device.ValueNumber = 9; device.Release.Set();
            Until(() => SlowResourceReadMonitor.Shared.ActiveCount == 0);
            Assert.AreEqual(0, events.Count, "A cancelled late value must not publish or revive the subscription.");
        }
        finally { device.Release.Set(); Until(() => SlowResourceReadMonitor.Shared.ActiveCount == 0); }
    }

    [TestMethod]
    public async Task WaitingAndSharedAcquisitionsDoNotRegisterAdditionalPhysicalReads()
    {
        using var scope = new MonitorScope();
        GlobalConfig.ResourceObservationIntervalMs = 60000;
        HubMaster.InjectHubToModule(this);
        var a = new BlockingDevice { BlockRead = 1 }; var b = new BlockingDevice { BlockRead = 1 };
        MyHub.TryAddThing(a, "/", "waiting" + Guid.NewGuid().ToString("N"));
        MyHub.TryAddThing(b, "/", "other" + Guid.NewGuid().ToString("N"));
        var resource = MyHub.GetLocalResouce(a.Path + "/Value");
        var first = Task.Run(() => MyHub.AcquireResourceObservation(resource));
        Task<ResourceObservationResult>? waiter = null;
        Task<ResourceObservationResult>? second = null;
        using var waiterStarted = new ManualResetEventSlim();
        try
        {
            Assert.IsTrue(a.Entered.Wait(2000));
            waiter = Task.Factory.StartNew(() => { waiterStarted.Set(); return MyHub.AcquireResourceObservation(resource); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.IsTrue(waiterStarted.Wait(2000));
            second = Task.Run(() => MyHub.AcquireResourceObservation(MyHub.GetLocalResouce(b.Path + "/Value")));
            Assert.IsTrue(b.Entered.Wait(2000));
            Until(() => scope.Log.Errors.Count(x => x.Contains("Get slow:")) == 2);
            Assert.IsFalse(waiter.IsCompleted); Assert.AreEqual(2, SlowResourceReadMonitor.Shared.ActiveCount);
            a.Release.Set(); b.Release.Set();
            var results = await Task.WhenAll(first, waiter, second).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(ResourceObservationStatus.Shared, results[1].Status);
            Assert.AreEqual(1, a.Reads); Assert.AreEqual(1, b.Reads);
            Until(() => SlowResourceReadMonitor.Shared.ActiveCount == 0);
            using var shared = MyHub.AcquireResourceObservation(resource).Handle;
            Assert.AreEqual(0, SlowResourceReadMonitor.Shared.ActiveCount); Assert.AreEqual(1, a.Reads);
            Assert.AreEqual(2, scope.Log.Errors.Count(x => x.Contains("Get slow:")));
            foreach (var result in results) result.Handle.Dispose();
        }
        finally
        {
            a.Release.Set(); b.Release.Set();
            foreach (var task in new[] { first, waiter, second }.Where(t => t != null))
                try { (await task!).Handle?.Dispose(); } catch { }
        }
    }

    public sealed class FailureDevice : Thing
    {
        public int Reads, BlockRead;
        public volatile int FailureFrom = int.MaxValue, ValueNumber = 1;
        public bool InvalidSample;
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        [Cfet2Status] public Status<int> Value
        {
            get
            {
                var read = Interlocked.Increment(ref Reads);
                if (read == BlockRead) { Entered.Set(); if (!Release.Wait(10000)) throw new TimeoutException("Test gate"); }
                if (read >= FailureFrom)
                {
                    if (InvalidSample) return new Status<int>(0, false);
                    throw new InvalidOperationException("Slow device failure");
                }
                return new Status<int>(ValueNumber);
            }
        }
    }
}

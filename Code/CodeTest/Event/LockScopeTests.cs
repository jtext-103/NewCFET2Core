using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Log;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event;

[TestClass, DoNotParallelize]
public class LockScopeTests : EventTransportTestBase
{
    [TestMethod]
    public async Task SlowSampleDoesNotBlockRegistrationPublicationOrStartupAndIsAdmittedAtCommit()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var delivered = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<string>();
        var sample = new EvaluatedSample(() => { entered.Set(); Await(release); return 42; });
        var publication = Task.Run(() => MyHub.EventHub.TryPublish(new EventArg("/slow", "alarm", sample)));
        try
        {
            Await(entered);
            await Task.Run(() =>
            {
                Assert.IsFalse(MyHub.IsLocalResourcesReady);
                MyHub.RegisterLocalEventObserver(ev => { observed.Enqueue(ev.Source); if (ev.Source == "/slow") delivered.Set(); });
                Assert.IsTrue(MyHub.EventHub.TryPublish(new EventArg("/before", "alarm", new Status<int>(1))));
                MyHub.StartThings();
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(publication.IsCompleted);
            release.Set();
            Assert.IsTrue(await publication.WaitAsync(TimeSpan.FromSeconds(2)));
            Await(delivered);
            CollectionAssert.AreEqual(new[] { "/slow" }, observed.ToArray());
            Assert.IsNull(sample.Timestamp, "Publication must not stamp the caller's sample.");
        }
        finally { release.Set(); await publication; }
    }

    [TestMethod]
    public async Task ShutdownDuringSampleEvaluationDoesNotWaitOrAdmitALateEvent()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sample = new EvaluatedSample(() => { entered.Set(); Await(release); return 42; });
        var publication = Task.Run(() => MyHub.EventHub.TryPublish(new EventArg("/slow", "alarm", sample)));
        try
        {
            Await(entered);
            await Task.Run(MyHub.EventHub.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(publication.IsCompleted);
            release.Set();
            Assert.IsFalse(await publication.WaitAsync(TimeSpan.FromSeconds(2)));
            var queue = Field<object>(MyHub.EventHub, "pendingLocalEvent");
            Assert.AreEqual(0, queue.GetType().GetProperty("Count")!.GetValue(queue));
        }
        finally { release.Set(); await publication; }
    }

    [TestMethod]
    public void AlreadyClosedHubDoesNotEvaluateSample()
    {
        MyHub.EventHub.Dispose();
        Assert.IsFalse(MyHub.EventHub.TryPublish(new EventArg("/closed", "alarm",
            new EvaluatedSample(() => throw new InvalidOperationException("Must not evaluate")))));
    }

    [TestMethod]
    public void OneResourceWaitingForAdmissionDoesNotBlockOtherObservationAcquisitionsOrReleases()
    {
        var device = new ObservationDevice();
        MyHub.TryAddThing(device, "/", "locks");
        MyHub.EventHub.ResourceObservationInterval = TimeSpan.FromMilliseconds(10);
        using var first = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/locks/A")).Handle;
        var other = MyHub.GetLocalResouce("/locks/B");
        var manager = Field<object>(MyHub.EventHub, "resourceObservationManager");
        var slots = Field<IDictionary>(manager, "slots");
        var slot = slots["/locks/A"]!;
        var stateGate = slot.GetType().GetField("StateGate")!.GetValue(slot)!;
        var admission = Field<object>(MyHub.EventHub, "lifecycleLock");
        Task? unrelated = null;
        Monitor.Enter(admission);
        try
        {
            Volatile.Write(ref device.AValue, 1);
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                if (!Monitor.TryEnter(stateGate)) return true;
                Monitor.Exit(stateGate); return false;
            }, 2000), "Polling must reach the admission boundary while its resource state is held.");
            unrelated = Task.Run(() =>
            {
                using var second = MyHub.AcquireResourceObservation(other).Handle;
                using var shared = MyHub.AcquireResourceObservation(other).Handle;
                Assert.AreEqual(2, MyHub.ObservedResourceCount);
            });
            Assert.IsTrue(unrelated.Wait(2000), "Resource B must not wait for resource A's admission.");
            Assert.AreEqual(1, MyHub.ObservedResourceCount);
        }
        finally
        {
            Monitor.Exit(admission);
            unrelated?.GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public async Task DirectoryValidationDoesNotBlockPublicationAndRechecksShutdown()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IEnumerable<ResourceBase> Directory()
        {
            entered.Set(); Await(release); yield break;
        }
        var validation = Task.Run(() => MyHub.EventHub.ValidateMinDeltaDirectory(Directory()));
        try
        {
            Await(entered);
            await Task.Run(() =>
            {
                Assert.IsTrue(MyHub.EventHub.TryPublish(new EventArg("/during-validation", "changed", new Status<int>(1))));
                MyHub.EventHub.Dispose();
            }).WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await validation);
        }
        finally { release.Set(); try { await validation; } catch (ObjectDisposedException) { } }
    }

    [TestMethod]
    public void ObservationLoopsMayCompleteBeforeShutdownReachesTheirSlot()
    {
        var device = new ObservationDevice();
        MyHub.TryAddThing(device, "/", "closing");
        MyHub.EventHub.ResourceObservationInterval = TimeSpan.FromMilliseconds(10);
        using var first = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/closing/A")).Handle;
        using var second = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/closing/B")).Handle;
        var manager = Field<object>(MyHub.EventHub, "resourceObservationManager");
        var slots = Field<IDictionary>(manager, "slots");
        var firstSlot = slots["/closing/A"]!;
        var secondSlot = slots["/closing/B"]!;
        var observation = secondSlot.GetType().GetField("Current")!.GetValue(secondSlot)!;
        var loop = (Task)observation.GetType().GetProperty("ObserveTask")!.GetValue(observation)!;
        var stateGate = firstSlot.GetType().GetField("StateGate")!.GetValue(firstSlot)!;
        Task? closing = null;
        Monitor.Enter(stateGate);
        try
        {
            closing = Task.Run(() => ((IDisposable)manager).Dispose());
            Assert.IsTrue(SpinWait.SpinUntil(() => Field<bool>(manager, "disposed"), 2000));
            Assert.IsTrue(loop.Wait(2000), "Other loops must see shutdown even before their slot is revoked.");
            Assert.IsFalse(closing.IsCompleted);
        }
        finally
        {
            Monitor.Exit(stateGate);
            Assert.IsTrue(closing!.Wait(2000), "Revoking a completed loop must not cancel a disposed CTS.");
        }
        Assert.AreEqual(0, MyHub.ObservedResourceCount);
    }

    [TestMethod]
    public void SlowReadFormattingRunsOutsideMonitorLock()
    {
        double time = 0;
        var lines = new List<string>();
        using var monitor = new SlowResourceReadMonitor(() => time, lines.Add);
        using (monitor.Begin("/slow", "poll", 10)) time = 20;
        var original = CultureInfo.CurrentCulture;
        var formattingCalls = 0;
        var outsideLock = true;
        try
        {
            CultureInfo.CurrentCulture = new FormattingCulture(() =>
            {
                formattingCalls++;
                outsideLock &= Task.Run(() =>
                {
                    using var other = monitor.Begin("/other", "poll", 100);
                }).Wait(2000);
            });
            monitor.Tick();
        }
        finally { CultureInfo.CurrentCulture = original; }
        Assert.IsTrue(formattingCalls > 0, "The culture must observe numeric formatting.");
        Assert.IsTrue(outsideLock, "Formatting must not prevent another resource read from starting or ending.");
        Assert.AreEqual(2, lines.Count);
        Assert.AreEqual(0, monitor.ActiveCount);
    }

    [TestMethod]
    public void ValidationLoggerRunsOutsideLifecycleAndValidationLocks()
    {
        GlobalConfig.ResourceMinDeltaOverrides = new[] { new ResourceMinDeltaOverride("/missing", 1) };
        using var hub = new EventHub();
        var warnings = 0;
        SetField(hub, "logger", new CallbackLogger(() =>
        {
            Interlocked.Increment(ref warnings);
            Assert.IsTrue(Task.Run(() =>
            {
                hub.ValidateMinDeltaDirectory(Array.Empty<ResourceBase>());
                Assert.IsTrue(hub.TryPublish(new EventArg("/logger", "changed", null)));
            }).Wait(2000));
        }));
        hub.ValidateMinDeltaDirectory(Array.Empty<ResourceBase>());
        Assert.AreEqual(1, warnings);
    }

    [TestMethod]
    public void RouteSnapshotsSurviveMutationDeduplicateAndStillRejectUnsubscribedHandlers()
    {
        var calls = 0;
        var hub = MyHub.EventHub;
        var filter = new EventFilter("/snapshot", "changed");
        filter.AddSourceAndType("/SNAPSHOT", "CHANGED");
        using var old = hub.Subscribe(filter, _ => Interlocked.Increment(ref calls));
        using var fallback = hub.Subscribe(new EventFilter("/.*", "changed"), _ => { });
        var message = new EventArg("/snapshot", "changed", null);
        var get = typeof(EventHub).GetMethod("getLocalCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshot = (IEnumerable<KeyValuePair<Guid, Subscriber>>)get.Invoke(hub, new object[] { message })!;
        using var added = hub.Subscribe(new EventFilter("/snapshot", "changed"), _ => { });
        old.Dispose();
        var saved = snapshot.ToArray();
        Assert.AreEqual(2, saved.Length, "The previous snapshot retains one exact candidate and one fallback.");
        Assert.AreEqual(old.Id, saved[0].Key);
        var current = ((IEnumerable<KeyValuePair<Guid, Subscriber>>)get.Invoke(hub, new object[] { message })!).ToArray();
        CollectionAssert.AreEquivalent(new[] { added.Id, fallback.Id }, current.Select(candidate => candidate.Key).ToArray());
        typeof(EventHub).GetMethod("invokeLocalHandlerIfSubscribed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(hub, new object[] { saved[0].Key, saved[0].Value, message });
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task RetentionCleanupDoesNotHoldSchedulerLockOrReinstallTimerAfterStop()
    {
        GlobalConfig.LogRetentionDays = 7;
        NlogProvider.StopRetentionScheduler();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var starts = 0;
        Action cleanup = () => { Interlocked.Increment(ref starts); entered.Set(); release.Wait(); finished.Set(); };
        var start = Task.Run(() => NlogProvider.StartRetentionScheduler(cleanup));
        try
        {
            Await(entered);
            await start.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Run(() =>
            {
                NlogProvider.StartRetentionScheduler(cleanup);
                NlogProvider.StopRetentionScheduler();
            }).WaitAsync(TimeSpan.FromSeconds(2));
            release.Set(); Await(finished);
            Assert.IsNull(typeof(NlogProvider).GetField("retentionCleanupTimer", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
            Assert.AreEqual(1, starts);
        }
        finally { release.Set(); await start; NlogProvider.StopRetentionScheduler(); Await(finished); }
    }

    private sealed class EvaluatedSample(Func<int> read) : SampleBase<int>
    {
        public override int Val => read();
    }

    private sealed class FormattingCulture(Action formatting) : CultureInfo("en-US")
    {
        public override NumberFormatInfo NumberFormat
        {
            get { formatting(); return InvariantCulture.NumberFormat; }
            set => base.NumberFormat = value;
        }
    }

    public sealed class ObservationDevice : Thing
    {
        public int AValue;
        [Cfet2Status] public int A => Volatile.Read(ref AValue);
        [Cfet2Status] public int B => 0;
    }

    private sealed class CallbackLogger(Action warn) : ICfet2Logger
    {
        public void Warn(string message) => warn();
        public void Error(string message) { }
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Fatal(string message) { }
    }
}

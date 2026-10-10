using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Sample;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    [DoNotParallelize]
    public class RegexResourceObservationTests : CFET2Host
    {
        [TestInitialize]
        public void Init()
        {
            GlobalConfig.ResourceObservationIntervalMs = 20;
            HubMaster.InjectHubToModule(this);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Hub.KillMaster();
            GlobalConfig.ResourceObservationIntervalMs = 500;
        }

        private int ObservedCount => (int)typeof(Hub).GetProperty("ObservedResourceCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(MyHub)!;
        private int SubscriptionCount => ((IDictionary)typeof(EventHub).GetProperty("localSubDict", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(MyHub.EventHub)!).Count;
        private void Interval(int milliseconds) => typeof(Hub).GetProperty("ResourceObservationInterval", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(MyHub, TimeSpan.FromMilliseconds(milliseconds));
        private static void Eventually(Func<bool> condition) => Assert.IsTrue(SpinWait.SpinUntil(condition, 3000), "Timed out waiting for observable behavior.");

        [TestMethod]
        public void MatchingUsesPairedFullCaseInsensitiveRulesWithoutReading()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", "device");
            var filter = new EventFilter("/DEVICE/Temperature", "alarm");
            filter.AddSourceAndType("/device/Pressure", "CHANGED");
            var matched = MyHub.MatchLocalResources(filter);
            CollectionAssert.AreEqual(new[] { "/device/Pressure" }, matched.Select(r => r.Path).ToArray());
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(0, ObservedCount);
            Assert.IsFalse(new EventFilter("/device", "changed").Matches("/device/Temperature", "changed"));
            Assert.IsFalse(new EventFilter().Matches("/device", "changed"));
        }

        [TestMethod]
        public void RegexAloneObservesBeforeStartAndDoesNotPublishInitialValues()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", "device");
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResources(new EventFilter("/device/(Temperature|Pressure)", "changed"), events.Enqueue);
            Assert.IsFalse(device.Started);
            Assert.IsTrue(subscription.HasLocalObservationResults);
            Assert.AreEqual(2, ObservedCount);
            Assert.IsTrue(subscription.ObservationResults.All(r => r.Status == ResourceObservationStatus.Started));
            Eventually(() => device.Reads >= 3);
            Assert.AreEqual(0, events.Count);
            device.TemperatureValue = 7;
            device.Pressure = 9;
            Eventually(() => events.Count == 2);
            Assert.IsTrue(events.All(e => e.Sample.Timestamp.HasValue));
            CollectionAssert.AreEquivalent(new[] { "/device/Temperature", "/device/Pressure" }, events.Select(e => e.Source).ToArray());
            subscription.Token.Dispose();
            Assert.AreEqual(0, ObservedCount);
            Assert.IsTrue(subscription.ObservationResults.All(r => r.Handle.IsReleased));
            subscription.Dispose();
        }

        [TestMethod]
        public void OverlappingRegexExactAndStandaloneHandlesShareAndReleaseIndependently()
        {
            Interval(60000);
            MyHub.TryAddThing(new Device(), "/", "device");
            var filter = new EventFilter("/device/(Temperature|Pressure)", "changed");
            filter.AddSourceAndType("/device/Temperature", ".*");
            using var a = MyHub.SubscribeResources(filter, _ => { });
            using var b = MyHub.SubscribeResources(new EventFilter("/device/Temp.*", "changed"), _ => { });
            using var exact = MyHub.SubscribeResource("/device/Temperature", _ => { });
            var direct = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/device/Temperature"));
            using var independent = direct.Handle;
            Assert.AreEqual(ResourceObservationStatus.Shared, direct.Status);
            Assert.AreEqual(2, a.ObservationResults.Count);
            Assert.AreEqual(ResourceObservationStatus.Shared, b.ObservationResults.Single().Status);
            Assert.AreEqual(2, ObservedCount);
            a.Token.Dispose();
            Assert.AreEqual(1, ObservedCount);
            b.Dispose();
            exact.Token.Dispose();
            Assert.AreEqual(1, ObservedCount);
            independent.Dispose();
            independent.Dispose();
            Assert.AreEqual(0, ObservedCount);
        }

        [TestMethod]
        public void SharingDoesNotResetTheExistingComparisonBaseline()
        {
            Interval(300);
            var device = new Device();
            MyHub.TryAddThing(device, "/", "device");
            var events = new ConcurrentQueue<EventArg>();
            using var a = MyHub.SubscribeResources(new EventFilter("/device/Temperature", "changed"), events.Enqueue);
            device.TemperatureValue = 3;
            using var exact = MyHub.SubscribeResource("/device/Temperature", _ => { });
            using var b = MyHub.SubscribeResources(new EventFilter("/device/Temp.*", "changed"), _ => { });
            Assert.AreEqual(3, exact.InitialValue.ObjectVal);
            Assert.AreEqual(ResourceObservationStatus.Shared, b.ObservationResults.Single().Status);
            Eventually(() => events.Count == 1);
            Assert.AreEqual(3, events.Single().Sample.ObjectVal, "Joining must not overwrite the original comparison baseline.");
        }

        [TestMethod]
        public void PartialFailuresAreReportedWithoutRetriesAndDoNotRestrictPublishedEvents()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", "device");
            var events = new ConcurrentQueue<EventArg>();
            using var group = MyHub.SubscribeResources(new EventFilter("/device/.*", ".*"), events.Enqueue);
            var results = group.ObservationResults.ToDictionary(r => r.ResourcePath);
            Assert.AreEqual(ResourceObservationStatus.Failed, results["/device/Broken"].Status);
            foreach (var name in new[] { "Native", "Complex", "NeedsInput", "Action" })
            {
                Assert.AreEqual(ResourceObservationStatus.Skipped, results["/device/" + name].Status);
                Assert.IsNull(results["/device/" + name].Handle);
            }
            Assert.AreEqual(0, device.NativeReads);
            Assert.AreEqual(0, device.UnsupportedReads);
            Assert.AreEqual(2, ObservedCount);
            device.Fail = false;
            Eventually(() => device.Reads >= 4);
            Assert.AreEqual(1, device.BrokenReads, "Initial failure must not be retried in the background.");
            foreach (var name in new[] { "Native", "Complex", "Missing", "Broken" })
                MyHub.Publish("/device/" + name, "alarm", 42);
            Eventually(() => events.Count == 4);
            using var retry = MyHub.SubscribeResources(new EventFilter("/device/Broken", "changed"), _ => { });
            Assert.AreEqual(ResourceObservationStatus.Started, retry.ObservationResults.Single().Status);
        }

        [TestMethod]
        public void AlarmOnlyAndZeroMatchesKeepEventSubscriptionsWithoutPolling()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", "device");
            var events = new ConcurrentQueue<EventArg>();
            using var alarm = MyHub.SubscribeResources(new EventFilter("/device/.*", "alarm"), events.Enqueue);
            using var empty = MyHub.SubscribeResources(new EventFilter("/missing/.*", "changed"), events.Enqueue);
            Assert.AreEqual(0, alarm.ObservationResults.Count);
            Assert.AreEqual(0, empty.ObservationResults.Count);
            Assert.AreEqual(0, device.Reads);
            MyHub.Publish("/device/Temperature", "alarm", 1);
            MyHub.Publish("/missing/value", "changed", 2);
            Eventually(() => events.Count == 2);
            Assert.AreEqual(0, ObservedCount);
        }

        [TestMethod]
        public void FilterIsSnapshottedAndDirectoryIsNotRescanned()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", "first");
            var filter = new EventFilter("/first/Temperature", "changed");
            var events = new ConcurrentQueue<EventArg>();
            using var group = MyHub.SubscribeResources(filter, events.Enqueue);
            filter.SourcesAndTypes[0].Source = "/other/.*";
            filter.AddSourceAndType("/other/.*", "alarm");
            MyHub.Publish("/first/Temperature", "changed", 10);
            Eventually(() => events.Count == 1);
            Assert.AreEqual(1, group.ObservationResults.Count);
            using var zero = MyHub.SubscribeResources(new EventFilter("/later/.*", "changed"), _ => { });
            MyHub.TryAddThing(new Device(), "/", "later");
            MyHub.StartThings();
            Assert.AreEqual(0, zero.ObservationResults.Count);
            Assert.AreEqual(1, ObservedCount);
        }

        [TestMethod]
        public void OrdinarySubscribeStillDoesNotObserveResources()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", "device");
            using var token = MyHub.Subscribe(new EventFilter("/device/.*", "changed"), _ => { });
            Assert.AreEqual(0, ObservedCount);
            Assert.AreEqual(0, device.Reads);
        }

        [TestMethod]
        public void InvalidFilterAndInitialMatchTimeoutLeaveNoSubscription()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", new string('a', 100) + "!");
            var invalid = new EventFilter();
            invalid.SourcesAndTypes.Add(new SourceAndTypeFilter { Source = "[", EventType = "changed" });
            Assert.Throws<ArgumentException>(() => MyHub.SubscribeResources(invalid, _ => { }));
            Assert.ThrowsExactly<ArgumentException>(() => MyHub.SubscribeResources(new EventFilter(), _ => { }));
            Assert.ThrowsExactly<RegexMatchTimeoutException>(() => MyHub.SubscribeResources(new EventFilter("^/(a+)+$", "changed"), _ => { }));
            Assert.AreEqual(0, SubscriptionCount);
            Assert.AreEqual(0, ObservedCount);
            Assert.AreEqual(0, device.Reads);
        }

        [TestMethod]
        public void DispatchTimeoutDoesNotUnsubscribeOrStopOtherEvents()
        {
            var badEvents = new ConcurrentQueue<EventArg>();
            var goodEvents = new ConcurrentQueue<EventArg>();
            using var bad = MyHub.SubscribeResources(new EventFilter("^/(a+)+$", "changed"), badEvents.Enqueue);
            using var good = MyHub.Subscribe(new EventFilter(".*", "changed"), goodEvents.Enqueue);
            MyHub.Publish("/" + new string('a', 100) + "!", "changed", 1);
            Eventually(() => goodEvents.Count == 1);
            MyHub.Publish("/aaa", "changed", 2);
            Eventually(() => badEvents.Count == 1 && goodEvents.Count == 2);
            Assert.AreEqual(2, SubscriptionCount);
            Assert.AreEqual(TimeSpan.FromMilliseconds(100), EventFilter.MatchTimeout);
            Assert.ThrowsExactly<RegexMatchTimeoutException>(() => new EventFilter(".*", "^(a+)+$").Matches("/x", new string('a', 100) + "!"));
        }

        [TestMethod]
        public void InvalidAdditionDoesNotCorruptAnExistingFilter()
        {
            var filter = new EventFilter("/a", "changed");
            Assert.Throws<ArgumentException>(() => filter.AddSourceAndType("[", "changed"));
            Assert.AreEqual(1, filter.SourcesAndTypes.Count);
            Assert.IsTrue(filter.Matches("/a", "changed"));
        }

        [TestMethod]
        public void RemoteGroupRoutesOriginalRulesWithoutLocalReadsOrClaimingResults()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", "device");
            var module = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(module);
            var filter = new EventFilter("/device/.*", "changed") { Host = "test://localhost:99" };
            using var group = MyHub.SubscribeResources(filter, _ => { });
            Assert.IsTrue(group.Token.IsRemote);
            Assert.IsFalse(group.HasLocalObservationResults);
            Assert.AreEqual(0, group.ObservationResults.Count);
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(0, ObservedCount);
            Assert.AreEqual("/device/.*", module.LastFilter!.SourcesAndTypes[0].Source);
            Assert.ThrowsExactly<ArgumentException>(() => MyHub.MatchLocalResources(filter));
            group.Dispose();
            Assert.AreEqual(1, module.UnsubscribeCount);
            module.ThrowOnSubscribe = true;
            Assert.ThrowsExactly<BadResourceRequestException>(() => MyHub.SubscribeResources(filter, _ => { }));
        }

        [TestMethod]
        public async Task ConcurrentAcquisitionsShareOneInitializationAndIndependentHandles()
        {
            Interval(60000);
            var device = new BlockingDevice { BlockRead = 1 };
            MyHub.TryAddThing(device, "/", "blocked");
            var resource = MyHub.GetLocalResouce("/blocked/Value");
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => MyHub.AcquireResourceObservation(resource))).ToArray();
            try
            {
                Assert.IsTrue(device.Entered.Wait(3000));
                device.Proceed.Set();
                var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1, results.Count(r => r.Status == ResourceObservationStatus.Started));
                Assert.AreEqual(7, results.Count(r => r.Status == ResourceObservationStatus.Shared));
                Assert.AreEqual(1, device.Reads);
                Assert.AreEqual(1, device.MaxActiveReads);
                Assert.AreEqual(8, results.Select(r => r.Handle.Id).Distinct().Count());
                foreach (var result in results.Take(7)) result.Handle.Dispose();
                Assert.AreEqual(1, ObservedCount);
                results[7].Handle.Dispose();
                Assert.AreEqual(0, ObservedCount);
            }
            finally { device.Proceed.Set(); }
        }

        [TestMethod]
        public async Task LastReleaseAndReacquireSerializeGettersAndSuppressStaleValue()
        {
            var device = new BlockingDevice { BlockRead = 2 };
            MyHub.TryAddThing(device, "/", "blocked");
            var resource = MyHub.GetLocalResouce("/blocked/Value");
            var events = new ConcurrentQueue<EventArg>();
            using var token = MyHub.Subscribe(new EventFilter("/blocked/Value", "changed"), events.Enqueue);
            var first = MyHub.AcquireResourceObservation(resource).Handle;
            try
            {
                Assert.IsTrue(device.Entered.Wait(3000));
                first.Dispose();
                var next = Task.Run(() => MyHub.AcquireResourceObservation(resource));
                device.ValueNumber = 7;
                device.Proceed.Set();
                var result = await next.WaitAsync(TimeSpan.FromSeconds(5));
                using var second = result.Handle;
                Eventually(() => device.Reads >= 5);
                Assert.AreEqual(0, events.Count, "The cancelled getter and new baseline must not publish changed.");
                Assert.AreEqual(1, device.MaxActiveReads);
                device.ValueNumber = 8;
                Eventually(() => events.Count == 1);
                Assert.AreEqual(8, events.Single().Sample.ObjectVal);
            }
            finally { device.Proceed.Set(); first.Dispose(); }
        }

        [TestMethod]
        public async Task ShutdownDuringInitializationCannotLeaveLateObservations()
        {
            var device = new BlockingDevice { BlockRead = 1 };
            MyHub.TryAddThing(new Device(), "/", "ready");
            MyHub.TryAddThing(device, "/", "blocked");
            var pending = Task.Run(() => MyHub.SubscribeResources(new EventFilter("/(ready/Temperature|blocked/Value)", "changed"), _ => { }));
            try
            {
                Assert.IsTrue(device.Entered.Wait(3000));
                Assert.AreEqual(1, ObservedCount, "The batch already owns a successfully initialized resource.");
                MyHub.EventHub.Dispose();
                device.Proceed.Set();
                await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(0, ObservedCount);
                Assert.AreEqual(0, SubscriptionCount);
            }
            finally { device.Proceed.Set(); }
        }

        [TestMethod]
        public async Task TokenUnsubscribeDuringInitializationReleasesTheLateHandle()
        {
            var device = new BlockingDevice { BlockRead = 1 };
            MyHub.TryAddThing(device, "/", "blocked");
            using var token = MyHub.Subscribe(new EventFilter("/blocked/Value", "changed"), _ => { });
            // Exercise the token/observation binding with a known token while its acquisition is in flight.
            var bind = typeof(EventHub).GetMethod("ObserveResource", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = Task.Run(() => bind.Invoke(MyHub.EventHub,
                new object?[] { token, MyHub.GetLocalResouce("/blocked/Value"), null }));
            try
            {
                Assert.IsTrue(device.Entered.Wait(3000));
                token.Dispose();
                device.Proceed.Set();
                var error = await Assert.ThrowsExactlyAsync<TargetInvocationException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsInstanceOfType<ObjectDisposedException>(error.InnerException);
                Assert.AreEqual(0, ObservedCount);
                Assert.AreEqual(0, SubscriptionCount);
            }
            finally { device.Proceed.Set(); }
        }

        [TestMethod]
        public void StandaloneAcquisitionEnforcesRegistrationAndObservationPolicy()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", "device");
            foreach (var path in new[] { "/device", "/device/Native", "/device/Complex", "/device/NeedsInput", "/device/Action" })
            {
                var result = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce(path));
                Assert.AreEqual(ResourceObservationStatus.Skipped, result.Status, path);
                Assert.IsNull(result.Handle);
                Assert.IsFalse(string.IsNullOrEmpty(result.Reason));
            }
            Assert.ThrowsExactly<ArgumentNullException>(() => MyHub.AcquireResourceObservation(null!));
            var unregistered = new Resource.ResourceStatus("unregistered", null!);
            Assert.ThrowsExactly<ArgumentException>(() => MyHub.AcquireResourceObservation(unregistered));
            Assert.AreEqual(0, device.NativeReads + device.UnsupportedReads);
            Assert.AreEqual(0, ObservedCount);
            Assert.AreEqual(0, SubscriptionCount);
        }

        [TestMethod]
        public void DeviceShutdownReleasesHandlesBeforeDeviceDispose()
        {
            var device = new Device();
            MyHub.TryAddThing(device, "/", "device");
            using var group = MyHub.SubscribeResources(new EventFilter("/device/Temperature", "changed"), _ => { });
            device.OnDispose = () => Assert.AreEqual(0, ObservedCount);
            MyHub.DisposeThings();
            Assert.IsTrue(device.Disposed);
            Assert.IsTrue(group.ObservationResults.Single().Handle.IsReleased);
            Assert.ThrowsExactly<ObjectDisposedException>(() => MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/device/Temperature")));
        }

        [TestMethod]
        public void PollingReadFailureKeepsObservationAndRecoversWithoutResubscribe()
        {
            var device = new Device { Fail = false };
            MyHub.TryAddThing(device, "/", "device");
            var events = new ConcurrentQueue<EventArg>();
            using var group = MyHub.SubscribeResources(new EventFilter("/device/Broken", "changed"), events.Enqueue);
            device.Fail = true;
            Eventually(() => device.BrokenReads >= 3);
            Assert.AreEqual(1, ObservedCount);
            Assert.AreEqual(0, events.Count);
            device.TemperatureValue = 8;
            device.Fail = false;
            Eventually(() => events.Count == 1);
            Assert.AreEqual(8, events.Single().Sample.ObjectVal);
        }

        public sealed class Device : Thing, IDisposable
        {
            public volatile int TemperatureValue = 1;
            public volatile bool Fail = true;
            public int Reads, BrokenReads, NativeReads, UnsupportedReads;
            public bool Started, Disposed;
            public Action? OnDispose;
            [Cfet2Status] public int Temperature { get { Interlocked.Increment(ref Reads); return TemperatureValue; } }
            [Cfet2Config] public int Pressure { get; set; } = 2;
            [Cfet2Status] public int Broken { get { Interlocked.Increment(ref BrokenReads); if (Fail) throw new InvalidOperationException("Not readable yet"); return TemperatureValue; } }
            [Cfet2Status, Cfet2Event] public int Native { get { Interlocked.Increment(ref NativeReads); return 0; } }
            [Cfet2Status] public int[] Complex { get { Interlocked.Increment(ref UnsupportedReads); return new[] { 1 }; } }
            [Cfet2Status] public int NeedsInput(int input) { Interlocked.Increment(ref UnsupportedReads); return input; }
            [Cfet2Method] public void Action() { }
            public override void Start() { Started = true; }
            public void Dispose() { OnDispose?.Invoke(); Disposed = true; }
        }

        public sealed class BlockingDevice : Thing
        {
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            public readonly ManualResetEventSlim Proceed = new ManualResetEventSlim();
            public int Reads, BlockRead, MaxActiveReads;
            private int activeReads;
            public volatile int ValueNumber = 1;
            [Cfet2Status]
            public int Value
            {
                get
                {
                    var active = Interlocked.Increment(ref activeReads);
                    int previous;
                    do { previous = MaxActiveReads; }
                    while (active > previous && Interlocked.CompareExchange(ref MaxActiveReads, active, previous) != previous);
                    try
                    {
                        if (Interlocked.Increment(ref Reads) == BlockRead)
                        {
                            Entered.Set();
                            if (!Proceed.Wait(5000)) throw new TimeoutException("Test getter release timed out.");
                        }
                        return ValueNumber;
                    }
                    finally { Interlocked.Decrement(ref activeReads); }
                }
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize]
    public class AutomaticObservationListTests : CFET2Host
    {
        private Dictionary<string, string> original = null!;
        private bool hostCreated;
        private static ObservationListRule Exact(string path) => new ObservationListRule(path: path);
        private static ObservationListRule Pattern(string regex) => new ObservationListRule(regex: regex);
        private int ObservedCount => (int)typeof(Hub).GetProperty("ObservedResourceCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(MyHub)!;
        private int SubscriptionCount => ((IDictionary)typeof(EventHub).GetProperty("localSubDict", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(MyHub.EventHub)!).Count;
        private static void Eventually(Func<bool> condition) => Assert.IsTrue(SpinWait.SpinUntil(condition, 3000));

        [TestInitialize]
        public void Setup()
        {
            original = GlobalConfig.Get();
            GlobalConfig.AutoObserveList = Array.Empty<ObservationListRule>();
            GlobalConfig.AutoDenyList = Array.Empty<ObservationListRule>();
            GlobalConfig.ResourceObservationIntervalMs = 60000;
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (hostCreated) Hub.KillMaster();
            GlobalConfig.Set(original);
        }

        private void CreateHost(params ObservationListRule[] observe)
        {
            GlobalConfig.AutoObserveList = observe;
            HubMaster.InjectHubToModule(this);
            hostCreated = true;
        }

        [TestMethod]
        public void ContainersRemainResidentAndBlacklistStillAllowsActivePublication()
        {
            GlobalConfig.ResourceObservationIntervalMs = 20;
            GlobalConfig.AutoDenyList = new[] { Exact("/containers/Denied") };
            CreateHost(Pattern("/containers/.*"));
            var device = new ContainerDevice();
            MyHub.TryAddThing(device, "/", "containers");
            MyHub.StartThings();
            Assert.AreEqual(3, ObservedCount);
            Assert.AreEqual(ResourceObservationStatus.Skipped, MyHub.AutoObservationResults.Single(r => r.ResourcePath == "/containers/Denied").Status);
            var events = new ConcurrentQueue<EventArg>();
            using (var subscription = MyHub.SubscribeResources(new EventFilter("/containers/.*", "changed"), events.Enqueue))
            {
                Assert.AreEqual(3, subscription.ObservationResults.Count(r => r.Status == ResourceObservationStatus.Shared));
                device.Array[0] = 2;
                device.List[0] = 2;
                device.Dictionary["a"] = 2;
                Eventually(() => events.Count == 3);
                MyHub.PublishResource("/containers/Denied", new[] { 9 });
                Eventually(() => events.Count == 4);
            }
            Assert.AreEqual(3, ObservedCount);
            using var listener = MyHub.Subscribe(new EventFilter("/containers/Array", "changed"), events.Enqueue);
            device.Array[0] = 3;
            Eventually(() => events.Count == 5);
        }

        public class ContainerDevice : Thing
        {
            [Cfet2Status] public int[] Array { get; } = new[] { 1 };
            [Cfet2Status] public List<int> List { get; } = new List<int> { 1 };
            [Cfet2Config] public Dictionary<string, int> Dictionary { get; set; } = new Dictionary<string, int> { ["a"] = 1 };
            [Cfet2Status] public int[] Denied { get; } = new[] { 1 };
        }

        [TestMethod]
        public void FileArraysAndDictionaryRoundTripPreserveRulesAndLegacyDefaults()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "{\"IP\":\"http://localhost\",\"Port\":1234,\"Accept\":\"json\",\"AutoObserveList\":[{\"Path\":\"/device/Value\"}],\"AutoDenyList\":[{\"Regex\":\"/hidden/.*\"}]}");
                GlobalConfig.Populate(file);
                Assert.AreEqual("1234", GlobalConfig.Port);
                Assert.AreEqual("/device/Value", GlobalConfig.AutoObserveList.Single().Path);
                var saved = GlobalConfig.Get();
                GlobalConfig.AutoDenyList = Array.Empty<ObservationListRule>();
                GlobalConfig.Set(saved);
                Assert.AreEqual("/hidden/.*", GlobalConfig.AutoDenyList.Single().Regex);
                var json = JObject.FromObject(saved);
                // New thresholds use JSON numbers; Get/Set retains its string dictionary contract.
                json["ResourceMinDelta"] = GlobalConfig.ResourceMinDelta;
                json["EventPublishTransports"] = Newtonsoft.Json.Linq.JArray.Parse(GlobalConfig.Get()["EventPublishTransports"]);
                json["WebSocketPortOffset"] = GlobalConfig.WebSocketPortOffset;
                File.WriteAllText(file, json.ToString());
                GlobalConfig.Populate(file);
                Assert.AreEqual("/device/Value", GlobalConfig.AutoObserveList.Single().Path);
                saved.Remove("AutoObserveList"); saved.Remove("AutoDenyList");
                GlobalConfig.Set(saved);
                Assert.AreEqual(0, GlobalConfig.AutoObserveList.Count);
                Assert.AreEqual(0, GlobalConfig.AutoDenyList.Count);
            }
            finally { File.Delete(file); }
        }

        [TestMethod]
        [DataRow("[null]")]
        [DataRow("[{}]")]
        [DataRow("[\"/device/Value\"]")]
        [DataRow("[{\"Path\":\"/device/Value\",\"Regex\":\".*\"}]")]
        [DataRow("[{\"Path\":\"http://localhost:80/device/Value\"}]")]
        [DataRow("[{\"Path\":\"/device/Value?q=1\"}]")]
        [DataRow("[{\"Regex\":\"[\"}]")]
        [DataRow("[{\"RegEx\":\".*\"}]")]
        [DataRow("[{\"Path\":null}]")]
        [DataRow("{}")]
        public void InvalidListDoesNotPartiallyApplyConfiguration(string invalid)
        {
            GlobalConfig.AutoObserveList = new[] { Exact("/old/Value") };
            var saved = GlobalConfig.Get();
            var update = new Dictionary<string, string>(saved) { ["IP"] = "changed", ["AutoDenyList"] = invalid };
            Assert.ThrowsExactly<ArgumentException>(() => GlobalConfig.Set(update));
            CollectionAssert.AreEquivalent(saved.ToArray(), GlobalConfig.Get().ToArray());
        }

        [TestMethod]
        public void BlacklistAppliesBeforeStartAcrossExactGroupAndIndependentAcquisitions()
        {
            GlobalConfig.AutoDenyList = new[] { Exact("/DEVICE/Value") };
            CreateHost();
            var device = new Device(); MyHub.TryAddThing(device, "/", "device");
            var events = new ConcurrentQueue<EventArg>();
            using var group = MyHub.SubscribeResources(new EventFilter("/device/Val.*", "changed"), events.Enqueue);
            var result = group.ObservationResults.Single();
            Assert.AreEqual(ResourceObservationStatus.Skipped, result.Status);
            Assert.AreEqual(ResourceObservationReason.DeniedByAutoDenyList, result.ReasonCode);
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(ResourceObservationStatus.Skipped, MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/device/Value")).Status);
            using var exact = MyHub.SubscribeResource("/device/Value", events.Enqueue);
            Assert.AreEqual(10, exact.InitialValue.ObjectVal);
            Assert.AreEqual(1, device.Reads, "Exact subscription still returns one explicit initial snapshot.");
            Assert.AreEqual(0, ObservedCount);
            var timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
            var sample = new Status<int>(42) { Timestamp = timestamp };
            MyHub.PublishResource("/device/Value", sample);
            Eventually(() => events.Count == 2);
            Assert.IsTrue(events.All(e => e.Sample.GetVal<int>() == 42 && e.Sample.Timestamp == timestamp));
            Assert.IsFalse(device.Started);
        }

        [TestMethod]
        public void ResidentDemandStartsAfterThingsSharesDeduplicatesAndOutlivesSubscribers()
        {
            CreateHost(Exact("/device/Value"), Pattern("/device/Val.*"));
            var device = new Device(); MyHub.TryAddThing(device, "/", "device");
            using var subscriber = MyHub.SubscribeResources(new EventFilter("/device/Value", "changed"), _ => { });
            Assert.AreEqual(1, device.Reads);
            MyHub.StartThings();
            Assert.IsTrue(device.Started);
            Assert.AreEqual(1, device.Reads, "Sharing must not reread the initial value.");
            Assert.AreEqual(ResourceObservationStatus.Shared, MyHub.AutoObservationResults.Single().Status);
            Assert.IsNull(MyHub.AutoObservationResults.Single().Handle);
            MyHub.EventHub.InitializeAutoObservations(new[] { MyHub.GetLocalResouce("/device/Value") });
            Assert.AreEqual(1, MyHub.AutoObservationResults.Count);
            subscriber.Dispose();
            Assert.AreEqual(0, SubscriptionCount);
            Assert.AreEqual(1, ObservedCount);
            device.OnDispose = () => Assert.AreEqual(0, ObservedCount);
            MyHub.DisposeThings();
            Assert.IsTrue(device.Disposed);
            Assert.AreEqual(0, ObservedCount);
        }

        [TestMethod]
        public void ResidentPollingPublishesChangesWithoutSubscriptionDemandAndPreservesBaseline()
        {
            GlobalConfig.ResourceObservationIntervalMs = 30;
            CreateHost(Exact("/device/Value"));
            var device = new Device { RequireStart = true }; MyHub.TryAddThing(device, "/", "device");
            MyHub.StartThings();
            Assert.AreEqual(0, SubscriptionCount);
            Eventually(() => device.Reads >= 3);
            var events = new ConcurrentQueue<EventArg>();
            using var token = MyHub.Subscribe(new EventFilter("/device/Value", "changed"), events.Enqueue);
            device.CurrentValue = 11;
            Eventually(() => events.Count == 1);
            Assert.AreEqual(11, events.Single().Sample.GetVal<int>());
            Assert.IsTrue(events.Single().Sample.Timestamp.HasValue);
            var reads = device.Reads;
            Eventually(() => device.Reads >= reads + 3);
            Assert.AreEqual(1, events.Count, "Unchanged values must not republish.");
        }

        [TestMethod]
        public void DenyWinsAndEligibilityFailuresDoNotReadOrPreventOtherResidents()
        {
            GlobalConfig.AutoDenyList = new[] { Exact("/device/Value") };
            CreateHost(Pattern("/device/.*"));
            var device = new Device(); MyHub.TryAddThing(device, "/", "device");
            MyHub.StartThings();
            var results = MyHub.AutoObservationResults.ToDictionary(r => r.ResourcePath);
            Assert.AreEqual(ResourceObservationReason.DeniedByAutoDenyList, results["/device/Value"].ReasonCode);
            foreach (var name in new[] { "Native", "Required", "Complex", "Action" })
                Assert.AreEqual(ResourceObservationStatus.Skipped, results["/device/" + name].Status, name);
            Assert.AreEqual(ResourceObservationStatus.Skipped, results["/device/Default"].Status);
            Assert.AreEqual(ResourceObservationStatus.Started, results["/device/Config"].Status);
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(0, device.IneligibleReads);
            Assert.AreEqual(0, device.LastDefault);
            Assert.AreEqual(1, ObservedCount);
        }

        [TestMethod]
        public void EmptyListsAndOutsideListResourcesRemainDemandDriven()
        {
            CreateHost(Exact("/missing/Value"));
            var device = new Device(); MyHub.TryAddThing(device, "/", "device");
            MyHub.StartThings();
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(0, ObservedCount);
            Assert.AreEqual(0, MyHub.AutoObservationResults.Count);
            using var subscription = MyHub.SubscribeResource("/device/Value", _ => { });
            Assert.AreEqual(1, ObservedCount);
            subscription.Dispose();
            Assert.AreEqual(0, ObservedCount);
        }

        [TestMethod]
        public void SnapshotRequiresRestartAndInputArrayCannotMutateIt()
        {
            var rules = new[] { Exact("/device/Value") };
            GlobalConfig.AutoDenyList = rules;
            CreateHost(Exact("/device/Config"));
            rules[0] = Exact("/device/Config");
            GlobalConfig.AutoDenyList = Array.Empty<ObservationListRule>();
            GlobalConfig.AutoObserveList = new[] { Exact("/device/Value") };
            MyHub.TryAddThing(new Device(), "/", "device");
            MyHub.StartThings();
            Assert.AreEqual("/device/Config", MyHub.AutoObservationResults.Single().ResourcePath);
            Assert.AreEqual("/device/Value", MyHub.ActiveObservationConfiguration.AutoDenyList.Single().Path);
            Assert.AreEqual(ResourceObservationStatus.Skipped, MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/device/Value")).Status);
            Hub.KillMaster(); hostCreated = false;
            HubMaster.InjectHubToModule(this); hostCreated = true;
            MyHub.TryAddThing(new Device(), "/", "device");
            MyHub.StartThings();
            Assert.AreEqual("/device/Value", MyHub.AutoObservationResults.Single().ResourcePath);
            Assert.AreEqual(0, MyHub.ActiveObservationConfiguration.AutoDenyList.Count);
        }

        [TestMethod]
        public void ExactPathsEscapeMetacharactersAndRegexUsesFullMatching()
        {
            CreateHost(Exact("/device.1/Value"), Pattern("/DEVICE2/Value"), Pattern("/device3"));
            foreach (var name in new[] { "device.1", "deviceX1", "device2", "device3" })
                MyHub.TryAddThing(new Device(), "/", name);
            MyHub.StartThings();
            CollectionAssert.AreEquivalent(new[] { "/device.1/Value", "/device2/Value" },
                MyHub.AutoObservationResults.Where(r => r.Status == ResourceObservationStatus.Started).Select(r => r.ResourcePath).ToArray());
            Assert.AreEqual(ResourceObservationStatus.Skipped, MyHub.AutoObservationResults.Single(r => r.ResourcePath == "/device3").Status);
        }

        [TestMethod]
        public void FailedInitialReadIsReportedOnceAndDoesNotBlockOtherResources()
        {
            CreateHost(Exact("/device/Value"), Exact("/device/Config"));
            var device = new Device { Fail = true }; MyHub.TryAddThing(device, "/", "device");
            MyHub.StartThings();
            Assert.AreEqual(1, ObservedCount);
            Assert.AreEqual(ResourceObservationStatus.Failed, MyHub.AutoObservationResults.Single(r => r.ResourcePath.EndsWith("/Value")).Status);
            device.Fail = false;
            MyHub.EventHub.InitializeAutoObservations(new[] { MyHub.GetLocalResouce("/device/Value") });
            Assert.AreEqual(1, device.Reads);
            using var retry = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/device/Value")).Handle;
            Assert.AreEqual(2, device.Reads);
            Assert.AreEqual(2, ObservedCount);
        }

        [TestMethod]
        public void RequiredParameterExactSubscriptionRetainsExistingFailureContract()
        {
            GlobalConfig.AutoDenyList = new[] { Exact("/device/Required") };
            CreateHost(); MyHub.TryAddThing(new Device(), "/", "device");
            Assert.ThrowsExactly<Jtext103.CFET2.Core.Exception.BadResourceRequestException>(() => MyHub.SubscribeResource("/device/Required", _ => { }));
            Assert.AreEqual(0, SubscriptionCount);
            Assert.AreEqual(0, ObservedCount);
        }

        [TestMethod]
        public void DenyMatchingTimeoutNeverAllowsObservation()
        {
            GlobalConfig.AutoDenyList = new[] { Pattern("^/(a+)+$") };
            CreateHost();
            var device = new Device(); var name = new string('a', 10000) + "!";
            MyHub.TryAddThing(device, "/", name);
            var result = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/" + name + "/Value"));
            Assert.AreEqual(ResourceObservationStatus.Failed, result.Status);
            Assert.AreEqual(ResourceObservationReason.ListMatchFailed, result.ReasonCode);
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(0, ObservedCount);
        }

        [TestMethod]
        public void PositiveMatchingTimeoutLeavesNoPartialResidentBatch()
        {
            CreateHost(Exact("/device/Value"), Pattern("^/(a+)+$"));
            var device = new Device(); MyHub.TryAddThing(device, "/", "device");
            MyHub.TryAddThing(new Device(), "/", new string('a', 10000) + "!");
            Assert.ThrowsExactly<System.Text.RegularExpressions.RegexMatchTimeoutException>(() => MyHub.StartThings());
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(0, ObservedCount);
        }

        [TestMethod]
        public async Task ShutdownDuringResidentInitialReadCannotLeaveALateHandle()
        {
            CreateHost(Exact("/device/Value"));
            var device = new Device { Block = true }; MyHub.TryAddThing(device, "/", "device");
            var starting = Task.Run(() => MyHub.StartThings());
            try
            {
                Assert.IsTrue(device.Entered.Wait(3000));
                MyHub.DisposeThings();
                device.Release.Set();
                await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await starting);
                Assert.AreEqual(0, ObservedCount);
                Assert.IsTrue(device.Disposed);
            }
            finally { device.Release.Set(); await Task.WhenAny(starting, Task.Delay(3000)); }
        }

        private sealed class Device : Thing, IDisposable
        {
            public int Reads, IneligibleReads, LastDefault;
            public volatile int CurrentValue = 10;
            public bool Started, Disposed, RequireStart, Fail, Block;
            public Action? OnDispose;
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            [Cfet2Status] public int Value
            {
                get
                {
                    Interlocked.Increment(ref Reads);
                    if (Block) { Entered.Set(); Release.Wait(); }
                    if (Fail || (RequireStart && !Started)) throw new InvalidOperationException("Not ready");
                    return CurrentValue;
                }
            }
            [Cfet2Config] public int Config { get; set; }
            [Cfet2Status, Cfet2Event] public int Native { get { IneligibleReads++; return 0; } }
            [Cfet2Status] public int Required(int channel) { IneligibleReads++; return channel; }
            [Cfet2Status] public int Default(int channel = 7) { LastDefault = channel; return channel; }
            [Cfet2Status] public object[] Complex { get { IneligibleReads++; return new object[] { 1 }; } }
            [Cfet2Method] public void Action() { }
            public override void Start() { Started = true; }
            public void Dispose() { OnDispose?.Invoke(); Disposed = true; }
        }
    }
}

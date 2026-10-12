#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize]
    public class MinDeltaTests : CFET2Host
    {
        private Dictionary<string, string> saved;
        private bool host;
        [TestInitialize] public void Init()
        {
            saved = GlobalConfig.Get();
            GlobalConfig.ResourceMinDelta = 1;
            GlobalConfig.ResourceMinDeltaOverrides = null;
            GlobalConfig.AutoObserveList = null;
            GlobalConfig.AutoDenyList = null;
            GlobalConfig.ResourceObservationIntervalMs = 20;
        }
        [TestCleanup] public void Cleanup() { if (host) Hub.KillMaster(); GlobalConfig.Set(saved); }
        private Device Mount()
        {
            HubMaster.InjectHubToModule(this); host = true;
            var device = new Device(); MyHub.TryAddThing(device, "/", "device"); return device;
        }
        private static bool Changed<T>(T left, T right, double delta) => ResourceValueComparison.HasChanged(
            ResourceValueComparison.Capture(left), ResourceValueComparison.Capture(right), delta);
        private static void Eventually(Func<bool> condition) => Assert.IsTrue(SpinWait.SpinUntil(condition, 3000));

        [TestMethod] public void NumericExtremesAndSpecialValues()
        {
            Assert.IsTrue(Changed(ulong.MaxValue - 1, ulong.MaxValue, 1));
            Assert.IsFalse(Changed(ulong.MaxValue - 1, ulong.MaxValue, 2));
            Assert.IsTrue(Changed(long.MinValue, long.MaxValue, 1e19));
            Assert.IsTrue(Changed(decimal.MinValue, decimal.MaxValue, 1e29));
            Assert.IsFalse(Changed(decimal.MinValue, decimal.MaxValue, double.MaxValue));
            Assert.IsTrue(Changed(0m, 1e-28m, double.Epsilon));
            Assert.IsFalse(Changed(0m, 0.05m, 0.05)); // Actual double threshold is slightly above decimal 0.05.
            Assert.IsFalse(Changed(double.NaN, double.NaN, 1));
            Assert.IsFalse(Changed(double.PositiveInfinity, double.PositiveInfinity, 1));
            Assert.IsTrue(Changed(double.NaN, 1d, double.MaxValue));
            Assert.IsTrue(Changed(double.NegativeInfinity, double.PositiveInfinity, 1));
            Assert.IsTrue(Changed(-double.MaxValue, double.MaxValue, double.MaxValue));
            Assert.IsFalse(Changed(-0d, 0d, 1));
            Assert.IsTrue(Changed<int?>(null, 1, 100));
            Assert.IsTrue(Changed(new[] { 1 }, new[] { 2 }, 100));
            Assert.IsTrue(Changed(DayOfWeek.Monday, DayOfWeek.Tuesday, 100));
            foreach (var invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Changed(1, 1, invalid));
        }

        [TestMethod] public void ConfigurationCopiesRoundTripsAndRejectsInvalidWithoutMutation()
        {
            var list = new List<ResourceMinDeltaOverride> { new ResourceMinDeltaOverride("/device/Value", 0.5) };
            GlobalConfig.ResourceMinDeltaOverrides = list; list.Clear();
            Assert.AreEqual(1, GlobalConfig.ResourceMinDeltaOverrides.Count);
            var config = GlobalConfig.Get(); GlobalConfig.ResourceMinDeltaOverrides = null; GlobalConfig.Set(config);
            Assert.AreEqual(0.5, GlobalConfig.ResourceMinDeltaOverrides.Single().MinDelta);
            foreach (var raw in new[] { "-1", "NaN", "Infinity", null })
            {
                var bad = new Dictionary<string, string>(config) { ["IP"] = "changed", ["ResourceMinDelta"] = raw };
                Assert.ThrowsExactly<ArgumentException>(() => GlobalConfig.Set(bad));
                Assert.AreEqual(config["IP"], GlobalConfig.IP);
                Assert.AreEqual(1d, GlobalConfig.ResourceMinDelta);
            }
            Assert.ThrowsExactly<ArgumentException>(() => GlobalConfig.ResourceMinDeltaOverrides = new[] {
                new ResourceMinDeltaOverride("/device/Value", 1), new ResourceMinDeltaOverride("/DEVICE/value", 2) });
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "{\"IP\":\"http://localhost\",\"Port\":1234,\"Accept\":\"json\",\"ResourceMinDelta\":\"1\"}");
                Assert.ThrowsExactly<ArgumentException>(() => GlobalConfig.Populate(file));
                File.WriteAllText(file, "{\"IP\":\"http://localhost\",\"Port\":1234,\"Accept\":\"json\",\"ResourceMinDelta\":0.05}");
                GlobalConfig.Populate(file); Assert.AreEqual(0.05, GlobalConfig.ResourceMinDelta);
            }
            finally { File.Delete(file); }
        }

        [TestMethod]
        [DataRow("exact")]
        [DataRow("combined")]
        [DataRow("handle")]
        [DataRow("resident")]
        public void AllLocalObservationEntrypointsAccumulateAgainstPublishedBaseline(string mode)
        {
            GlobalConfig.ResourceMinDeltaOverrides = new[] { new ResourceMinDeltaOverride("/DEVICE/value", 2) };
            if (mode == "resident") GlobalConfig.AutoObserveList = new[] { new ObservationListRule(path: "/device/Value") };
            var device = Mount();
            GlobalConfig.ResourceMinDelta = 999; GlobalConfig.ResourceMinDeltaOverrides = null;
            var events = new ConcurrentQueue<EventArg>();
            using var listener = MyHub.Subscribe(new EventFilter("/device/Value", "changed"), events.Enqueue);
            IDisposable demand = mode == "exact" ? MyHub.SubscribeResource("/device/Value", _ => { }) :
                mode == "combined" ? MyHub.SubscribeResources(new EventFilter("/device/Value", "changed"), _ => { }) :
                mode == "handle" ? MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/device/Value")).Handle : null;
            try
            {
                if (mode == "resident") MyHub.StartThings();
                var reads = device.Reads; Eventually(() => device.Reads >= reads + 2); Assert.AreEqual(0, events.Count);
                device.Value = 11; reads = device.Reads; Eventually(() => device.Reads >= reads + 3); Assert.AreEqual(0, events.Count);
                device.Value = 12; Eventually(() => events.Count == 1); Assert.AreEqual(12d, events.Single().Sample.ObjectVal);
                device.Value = 11; reads = device.Reads; Eventually(() => device.Reads >= reads + 3); Assert.AreEqual(1, events.Count);
                device.Value = 10; Eventually(() => events.Count == 2);
            }
            finally { demand?.Dispose(); }
        }

        [TestMethod] public void ActiveChannelsUseSnapshotOverrideAndDirectPublicationBypassesThreshold()
        {
            GlobalConfig.ResourceMinDeltaOverrides = new[] { new ResourceMinDeltaOverride("/device/Channel", 2) };
            var device = Mount(); GlobalConfig.ResourceMinDeltaOverrides = null;
            Assert.IsTrue(device.PublishResourceIfChanged("Channel", 10d, new object[] { 1 }));
            Assert.IsFalse(device.PublishResourceIfChanged("Channel", 11d, new object[] { 1 }));
            Assert.IsTrue(device.PublishResourceIfChanged("Channel", 11d, new object[] { 2 }));
            Assert.IsTrue(device.PublishResourceIfChanged("Channel", 12d, new object[] { 1 }));
            var events = new ConcurrentQueue<EventArg>();
            using var listener = MyHub.Subscribe(new EventFilter("/device/Value", "changed"), events.Enqueue);
            Assert.IsTrue(device.PublishResourceIfChanged("Value", 10d)); Eventually(() => events.Count == 1);
            MyHub.PublishResource("/device/Value", 10.1d); Eventually(() => events.Count == 2);
            Assert.IsFalse(device.PublishResourceIfChanged("Value", 10.1d));
        }

        public class Device : Thing
        {
            public double Value = 10;
            public int Reads;
            [Cfet2Status("Value")] public double Read() { Interlocked.Increment(ref Reads); return Value; }
            [Cfet2Status] public double Channel(int channel) => Value;
        }

        [TestMethod] public void SharedObservationSurvivesReleaseAndReinitializesAfterLastRelease()
        {
            var device = Mount();
            var events = new ConcurrentQueue<EventArg>();
            using var listener = MyHub.Subscribe(new EventFilter("/device/Value", "changed"), events.Enqueue);
            var first = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/device/Value")).Handle;
            using var second = MyHub.SubscribeResources(new EventFilter("/device/Value", "changed"), _ => { });
            Assert.AreEqual(ResourceObservationStatus.Shared, second.ObservationResults.Single().Status);
            device.Value = 10.4; var reads = device.Reads; Eventually(() => device.Reads >= reads + 3);
            first.Dispose(); device.Value = 11; Eventually(() => events.Count == 1);
            second.Dispose();
            device.Value = 20;
            using var restarted = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/device/Value")).Handle;
            reads = device.Reads; Eventually(() => device.Reads >= reads + 3); Assert.AreEqual(1, events.Count);
            device.Value = 21; Eventually(() => events.Count == 2);
        }

        [TestMethod] public void StartPublicationUsesCreationSnapshotAndNewEventHubUsesNewConfig()
        {
            HubMaster.InjectHubToModule(this); host = true;
            var device = new StartingDevice(); MyHub.TryAddThing(device, "/", "device");
            GlobalConfig.ResourceMinDelta = 0;
            MyHub.StartThings();
            Assert.IsTrue(device.First); Assert.IsFalse(device.Small); Assert.IsTrue(device.Boundary);
            using var fresh = new EventHub();
            Assert.AreEqual(0d, fresh.ResolveMinDelta(MyHub.GetLocalResouce("/device/Value")));
        }

        public class StartingDevice : Thing
        {
            [Cfet2Status] public double Value => 10;
            public bool First, Small, Boundary;
            public override void Start()
            {
                First = PublishResourceIfChanged("Value", 10d);
                Small = PublishResourceIfChanged("Value", 10.5d);
                Boundary = PublishResourceIfChanged("Value", 11d);
            }
        }

        [TestMethod] public void ScalarFamiliesZeroCompatibilityAndConcurrentActiveBaseline()
        {
            foreach (var type in new[] { typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int),
                typeof(uint), typeof(long), typeof(ulong), typeof(decimal), typeof(float), typeof(double) })
            {
                var capture = typeof(ResourceValueComparison).GetMethod("Capture").MakeGenericMethod(type);
                var a = (ResourceValueSnapshot)capture.Invoke(null, new[] { Convert.ChangeType(10, type) });
                var b = (ResourceValueSnapshot)capture.Invoke(null, new[] { Convert.ChangeType(11, type) });
                Assert.IsTrue(ResourceValueComparison.HasChanged(a, b, 1));
                Assert.IsFalse(ResourceValueComparison.HasChanged(a, b, 2));
                Assert.AreEqual(ResourceValueComparison.HasChanged(a, b), ResourceValueComparison.HasChanged(a, b, 0));
            }
            var device = Mount(); Assert.IsTrue(device.PublishResourceIfChanged("Value", 10d));
            Assert.IsFalse(device.PublishResourceIfChanged("Value", 10.4d));
            var accepted = 0;
            System.Threading.Tasks.Parallel.For(0, 30, _ => { if (device.PublishResourceIfChanged("Value", 11d)) Interlocked.Increment(ref accepted); });
            Assert.AreEqual(1, accepted);
            Assert.ThrowsExactly<NotSupportedException>(() => device.PublishResourceIfChanged("Value", new object()));
            Assert.IsFalse(device.PublishResourceIfChanged("Value", 11d));
            MyHub.EventHub.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => device.PublishResourceIfChanged("Value", 12d));
        }

        [TestMethod] public void OverrideErrorsIdentifyIndexAndPreserveConfiguration()
        {
            var original = GlobalConfig.Get();
            foreach (var raw in new[] { "[null]", "[{\"Path\":\"/x\"}]", "[{\"Path\":\"/x\",\"MinDelta\":\"1\"}]",
                "[{\"Path\":\"/x\",\"MinDelta\":1,\"Extra\":1}]", "[{\"Path\":\"/x\",\"MinDelta\":-1}]" })
            {
                var config = new Dictionary<string, string>(original) { ["ResourceMinDeltaOverrides"] = raw, ["ResourceMinDelta"] = "9" };
                var error = Assert.ThrowsExactly<ArgumentException>(() => GlobalConfig.Set(config));
                StringAssert.Contains(error.Message, "ResourceMinDeltaOverrides[0]");
                Assert.AreEqual(1d, GlobalConfig.ResourceMinDelta);
            }
            Assert.ThrowsExactly<ArgumentException>(() => new ResourceMinDeltaOverride(" ", 1));
            Assert.ThrowsExactly<ArgumentException>(() => GlobalConfig.ResourceMinDeltaOverrides = new ResourceMinDeltaOverride[] { null });
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GlobalConfig.ResourceMinDelta = double.NaN);
            GlobalConfig.ResourceMinDelta = -0d; Assert.AreEqual(0L, BitConverter.DoubleToInt64Bits(GlobalConfig.ResourceMinDelta));
        }

        [TestMethod] public void DirectoryDiagnosticsWarnOnceWithoutReadingOrStartingObservation()
        {
            GlobalConfig.ResourceMinDeltaOverrides = new[] {
                new ResourceMinDeltaOverride("/device", 1), new ResourceMinDeltaOverride("/missing", 1),
                new ResourceMinDeltaOverride("/device/Value/", 1), new ResourceMinDeltaOverride("/device/Channel/1", 1),
                new ResourceMinDeltaOverride("/device/Value", 1) };
            var device = Mount();
            var log = new DiagnosticLog();
            typeof(EventHub).GetField("logger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(MyHub.EventHub, log);
            MyHub.StartThings();
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(0, MyHub.ObservedResourceCount);
            Assert.AreEqual(4, log.Warnings.Count);
            MyHub.EventHub.ValidateMinDeltaDirectory(new[] { MyHub.GetLocalResouce("/device/Value") });
            Assert.AreEqual(4, log.Warnings.Count);
        }

        private sealed class DiagnosticLog : Jtext103.CFET2.Core.Log.ICfet2Logger
        {
            public readonly List<string> Warnings = new List<string>();
            public void Trace(string message) { }
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) => Warnings.Add(message);
            public void Error(string message) { }
            public void Fatal(string message) { }
        }
    }
}

#nullable disable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize]
    public class ChangePublicationTests : CFET2Host
    {
        [TestInitialize] public void Init() => HubMaster.InjectHubToModule(this);
        [TestCleanup] public void Cleanup() => Hub.KillMaster();
        private static void Eventually(Func<bool> predicate) => Assert.IsTrue(SpinWait.SpinUntil(predicate, 3000));
        private Device Mount(string name = "device") { var device = new Device(); MyHub.TryAddThing(device, "/", name); return device; }

        [TestMethod]
        public void PublicSnapshotsAreIndependentAndSupportContainersAndNull()
        {
            var data = new Dictionary<string, List<int[]>> { ["a"] = new List<int[]> { new[] { 1 } } };
            var first = ResourceValueComparison.Capture(data);
            Assert.IsFalse(ResourceValueComparison.HasChanged(first, ResourceValueComparison.Capture(data)));
            data["a"][0][0] = 2;
            Assert.IsTrue(ResourceValueComparison.HasChanged(first, ResourceValueComparison.Capture(data)));
            Assert.IsFalse(ResourceValueComparison.HasChanged(ResourceValueComparison.Capture<int?>(null), ResourceValueComparison.Capture<int?>(null)));
            Assert.IsTrue(ResourceValueComparison.HasChanged(ResourceValueComparison.Capture(new int[1, 2]), ResourceValueComparison.Capture(new int[2, 1])));
            Assert.ThrowsExactly<ArgumentNullException>(() => ResourceValueComparison.HasChanged(null, first));
            Assert.ThrowsExactly<ArgumentNullException>(() => ResourceValueComparison.HasChanged(first, null));
        }

        [TestMethod]
        public void PublicComparisonRejectsDeclaredAndRuntimeTypesWithNestedLocations()
        {
            var error = Assert.ThrowsExactly<NotSupportedException>(() => ResourceValueComparison.Capture(new Dictionary<string, List<CustomValue>>()));
            StringAssert.Contains(error.Message, "Dictionary.Value.List.Element");
            StringAssert.Contains(error.Message, "CustomValue");
            Assert.ThrowsExactly<NotSupportedException>(() => ResourceValueComparison.Capture<object>(new[] { 1 }));
            Assert.ThrowsExactly<NotSupportedException>(() => ResourceValueComparison.Capture<List<object>>(null));
            Assert.ThrowsExactly<NotSupportedException>(() => ResourceValueComparison.Capture<IList<int>>(new List<int>()));
            Assert.ThrowsExactly<NotSupportedException>(() => ResourceValueComparison.Capture<List<int>>(new DerivedList()));
            Assert.ThrowsExactly<NotSupportedException>(() => ResourceValueComparison.Capture(new List<List<int>> { new DerivedList() }));
            Assert.ThrowsExactly<NotSupportedException>(() => ResourceValueComparison.Capture(new Status<int>(1)));
        }

        [TestMethod]
        public void TypeResultsCacheSupportedAndUnsupportedTypesAcrossConcurrentCalls()
        {
            var type = typeof(Hub).Assembly.GetType("Jtext103.CFET2.Core.Event.ValueTypeValidation");
            var get = type.GetMethod("Get", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var candidate in new[] { typeof(List<int[]>), typeof(List<CustomValue>) })
            {
                var first = get.Invoke(null, new object[] { candidate });
                Parallel.For(0, 30, _ => Assert.AreSame(first, get.Invoke(null, new object[] { candidate })));
            }
        }

        [TestMethod]
        public void PublicCaptureSupportsEveryExistingScalarAndNullableType()
        {
            var capture = typeof(ResourceValueComparison).GetMethod("Capture");
            var types = new[] { typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
                typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(bool), typeof(char),
                typeof(string), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid), typeof(DayOfWeek) };
            foreach (var type in types)
            {
                var value = type.IsValueType ? Activator.CreateInstance(type) : "text";
                var method = capture.MakeGenericMethod(type);
                var baseline = (ResourceValueSnapshot)method.Invoke(null, new[] { value });
                Assert.IsFalse(ResourceValueComparison.HasChanged(baseline, (ResourceValueSnapshot)method.Invoke(null, new[] { value })));
                if (type.IsValueType)
                {
                    var nullable = capture.MakeGenericMethod(typeof(Nullable<>).MakeGenericType(type));
                    Assert.IsFalse(ResourceValueComparison.HasChanged((ResourceValueSnapshot)nullable.Invoke(null, new object[] { null }),
                        (ResourceValueSnapshot)nullable.Invoke(null, new object[] { null })));
                }
            }
        }

        [TestMethod]
        public void SnapshotCopyFailureDoesNotPublishOrAdvanceBaseline()
        {
            var device = Mount();
            var comparer = new FailingComparer();
            var values = new Dictionary<string, int>(comparer) { ["a"] = 1 };
            Assert.IsTrue(device.PublishResourceIfChanged("Value", values));
            comparer.Fail = true;
            Assert.ThrowsExactly<InvalidOperationException>(() => device.PublishResourceIfChanged("Value", values));
            comparer.Fail = false;
            Assert.IsFalse(device.PublishResourceIfChanged("Value", values));
            values["a"] = 2;
            Assert.IsTrue(device.PublishResourceIfChanged("Value", values));
        }

        [TestMethod]
        public void FirstNullPublishesAndTimestampOnlyChangesDoNotPublish()
        {
            var device = Mount();
            var events = new ConcurrentQueue<EventArg>();
            using var listener = MyHub.Subscribe(new EventFilter("/device/Value", "changed"), events.Enqueue);
            var sourceTime = new DateTimeOffset(2026, 10, 10, 1, 0, 0, TimeSpan.Zero);
            Assert.IsTrue(device.PublishResourceIfChanged<string>("Value", null, timestamp: sourceTime));
            Assert.IsFalse(device.PublishResourceIfChanged<string>("Value", null, timestamp: sourceTime.AddHours(1)));
            Eventually(() => events.Count == 1);
            Assert.IsNull(events.Single().Sample.ObjectVal);
            Assert.AreEqual(sourceTime, events.Single().Sample.Timestamp);
            Assert.AreEqual(0, device.Reads);
        }

        [TestMethod]
        public void PublicationUsesIndependentValuesAndBaselines()
        {
            var device = Mount();
            var events = new ConcurrentQueue<EventArg>();
            using var listener = MyHub.Subscribe(new EventFilter("/device/Value", "changed"), events.Enqueue);
            var values = new List<int> { 1 };
            Assert.IsTrue(device.PublishResourceIfChanged("Value", values));
            Eventually(() => events.Count == 1);
            var published = (List<int>)events.Single().Sample.ObjectVal;
            values[0] = 2;
            Assert.AreEqual(1, published[0]);
            published[0] = 99;
            Assert.IsFalse(device.PublishResourceIfChanged("Value", new List<int> { 1 }));
            Assert.IsTrue(device.PublishResourceIfChanged("Value", values));
            Assert.IsNotNull(events.ToArray()[0].Sample.Timestamp);
        }

        [TestMethod]
        public void ChannelsAliasesAndDefaultsHaveIndependentCanonicalPaths()
        {
            var device = Mount();
            var events = new ConcurrentQueue<EventArg>();
            using var listener = MyHub.Subscribe(new EventFilter("/device/.*", "changed"), events.Enqueue);
            Assert.IsTrue(device.PublishResourceIfChanged("Temperature", 10, new object[] { 1 }));
            Assert.IsTrue(device.PublishResourceIfChanged("temp", 10, new object[] { 2 }));
            Assert.IsFalse(device.PublishResourceIfChanged("Temperature", 10, new object[] { "1" }));
            Assert.IsTrue(device.PublishResourceIfChanged("Default", 7));
            Assert.IsFalse(device.PublishResourceIfChanged("Default", 7, new object[] { 7 }));
            Assert.AreEqual(0, device.Reads);
            Eventually(() => events.Count == 3);
            CollectionAssert.AreEquivalent(new[] { "/device/temp/1", "/device/temp/2", "/device/Default/7" }, events.Select(e => e.Source).ToArray());
            foreach (var e in events) Assert.IsTrue(MyHub.Get(e.Source).IsValid);
        }

        [TestMethod]
        public void MultipleAndParamsArgumentsRoundTripWithoutCallingGetters()
        {
            var device = Mount();
            var events = new ConcurrentQueue<EventArg>();
            using var listener = MyHub.Subscribe(new EventFilter("/device/.*", "changed"), events.Enqueue);
            Assert.IsTrue(device.PublishResourceIfChanged("Multiple", 1, new object[] { 3, "abc" }));
            Assert.IsTrue(device.PublishResourceIfChanged("Variadic", 1, new object[] { 2, 4, 6 }));
            Assert.IsTrue(device.PublishResourceIfChanged("Variadic", 1, new object[] { 2 }));
            Assert.AreEqual(0, device.Reads);
            Eventually(() => events.Count == 3);
            Assert.AreEqual("3:abc", MyHub.Get("/device/Multiple/3/abc").ObjectVal);
            Assert.AreEqual("2:4,6", MyHub.Get("/device/Variadic/2/4/6").ObjectVal);
            Assert.AreEqual("2:", MyHub.Get("/device/Variadic/2").ObjectVal);
        }

        [TestMethod]
        public void InvalidResourceAndArgumentsFailWithoutPublication()
        {
            var device = Mount();
            Assert.ThrowsExactly<ArgumentException>(() => device.PublishResourceIfChanged("Missing", 1));
            Assert.ThrowsExactly<ArgumentException>(() => device.PublishResourceIfChanged("Action", 1));
            Assert.ThrowsExactly<ArgumentException>(() => device.PublishResourceIfChanged("Temperature", 1));
            Assert.ThrowsExactly<ArgumentException>(() => device.PublishResourceIfChanged("Temperature", 1, new object[] { 1, 2 }));
            Assert.ThrowsExactly<ArgumentException>(() => device.PublishResourceIfChanged("Temperature", 1, new object[] { "bad" }));
            Assert.ThrowsExactly<ArgumentException>(() => device.PublishResourceIfChanged("Multiple", 1, new object[] { 1, "" }));
            Assert.ThrowsExactly<ArgumentException>(() => device.PublishResourceIfChanged("Multiple", 1, new object[] { 1, null }));
            Assert.AreEqual(0, device.Reads);
        }

        [TestMethod]
        public void FailedCaptureDoesNotChangeBaselineAndErrorsIncludeCurrentPath()
        {
            var device = Mount();
            Assert.IsTrue(device.PublishResourceIfChanged("Value", new List<int> { 1 }));
            var error = Assert.ThrowsExactly<NotSupportedException>(() => device.PublishResourceIfChanged<List<int>>("Value", new DerivedList { 2 }));
            StringAssert.Contains(error.Message, "/device/Value");
            Assert.IsFalse(device.PublishResourceIfChanged("Value", new List<int> { 1 }));
            Assert.IsTrue(device.PublishResourceIfChanged("Value", new List<int> { 2 }));
            var other = Mount("other");
            var second = Assert.ThrowsExactly<NotSupportedException>(() => other.PublishResourceIfChanged("Value", new CustomValue()));
            StringAssert.Contains(second.Message, "/other/Value");
            Assert.IsFalse(second.Message.Contains("/device/Value"));
        }

        [TestMethod]
        public void ConcurrentSamePathPublishesOnlyOnceAndConfigIsSupported()
        {
            var device = Mount();
            int accepted = 0;
            Parallel.For(0, 50, _ => { if (device.PublishResourceIfChanged("Config", new[] { 1 })) Interlocked.Increment(ref accepted); });
            Assert.AreEqual(1, accepted);
        }

        [TestMethod]
        public void ClosedHubAndUnregisteredThingRejectAndRemountStartsFresh()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => new Device().PublishResourceIfChanged("Value", 1));
            var device = Mount();
            Assert.IsTrue(device.PublishResourceIfChanged("Value", 1));
            var oldHub = device.MyHub;
            Hub.KillMaster();
            Assert.ThrowsExactly<ObjectDisposedException>(() => device.PublishResourceIfChanged("Value", 1));
            HubMaster.InjectHubToModule(this);
            MyHub.TryAddThing(device, "/", "newMount");
            Assert.IsTrue(device.PublishResourceIfChanged("Value", 1));
            var method = typeof(Hub).GetMethod("PublishThingResourceIfChanged", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(typeof(int));
            var failure = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(oldHub, new object[] { device, "Value", 2, null, null }));
            Assert.IsInstanceOfType<ObjectDisposedException>(failure.InnerException);
        }

        public class Device : Thing
        {
            public int Reads;
            [Cfet2Status] public string Value { get { Reads++; return "value"; } }
            [Cfet2Status(Name = "temp")] public int Temperature(int channel) { Reads++; return channel; }
            [Cfet2Status] public int Default(int channel = 7) { Reads++; return channel; }
            [Cfet2Status] public string Multiple(int channel, string name) { Reads++; return channel + ":" + name; }
            [Cfet2Status] public string Variadic(int channel, params int[] values) { Reads++; return channel + ":" + string.Join(",", values); }
            [Cfet2Config] public int[] Config { get; set; } = new[] { 1 };
            [Cfet2Method] public void Action() { }
        }
        public class CustomValue { }
        public class DerivedList : List<int> { }
        public class FailingComparer : IEqualityComparer<string>
        {
            public bool Fail;
            public bool Equals(string left, string right) => left == right;
            public int GetHashCode(string value)
            {
                if (Fail) throw new InvalidOperationException("Snapshot copy failure.");
                return value.GetHashCode();
            }
        }
    }
}

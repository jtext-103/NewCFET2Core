#nullable disable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class ResourceObservationTypeTest : CFET2Host
    {
        private int previousInterval;

        [TestInitialize]
        public void Init()
        {
            previousInterval = GlobalConfig.ResourceObservationIntervalMs;
            GlobalConfig.ResourceObservationIntervalMs = 30;
            HubMaster.InjectHubToModule(this);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Hub.KillMaster();
            GlobalConfig.ResourceObservationIntervalMs = previousInterval;
        }

        [TestMethod]
        public void ScalarAndNullableDeclarationsShouldSupportPropertiesAndMethods()
        {
            var types = new[] { typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
                typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double),
                typeof(decimal), typeof(bool), typeof(char), typeof(string), typeof(DateTime),
                typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid), typeof(DayOfWeek) };
            int index = 0;
            foreach (var type in types)
            {
                AssertSupported(type, index++);
                if (type.IsValueType)
                    AssertSupported(typeof(Nullable<>).MakeGenericType(type), index++);
            }
            Assert.AreEqual(0, CountObservations());
        }

        private void AssertSupported(Type type, int index)
        {
            var thing = (Thing)Activator.CreateInstance(typeof(ScalarThing<>).MakeGenericType(type));
            var path = "/scalar" + index;
            MyHub.TryAddThing(thing, "/", "scalar" + index);
            foreach (var member in new[] { "Value", "Read" })
            {
                using var subscription = MyHub.SubscribeResource(path + "/" + member, _ => { });
                Assert.IsTrue(subscription.InitialValue.IsValid, type.ToString());
            }
        }

        [TestMethod]
        public void ContainerDeclarationsShouldSupportPropertiesAndMethods()
        {
            var types = new[] { typeof(int[]), typeof(int?[,]), typeof(string[][]), typeof(List<string>),
                typeof(List<int?>), typeof(Dictionary<string, int[]>), typeof(Dictionary<int?, string>),
                typeof(List<Dictionary<string, List<DateTimeOffset?>>>) };
            for (int index = 0; index < types.Length; index++) AssertSupported(types[index], index);
            Assert.AreEqual(0, CountObservations());
        }

        [TestMethod]
        public void ComplexAndAmbiguousDeclarationsShouldRejectBeforeGetterOrSubscription()
        {
            var types = new[] { typeof(ComplexValue[]), typeof(List<ComplexValue>), typeof(Dictionary<string, ComplexValue>),
                typeof(ComplexValue), typeof(ComplexStruct), typeof(object), typeof(ISample),
                typeof(Status<object[]>), typeof(SampleBase<object>), typeof(List<object>), typeof(Dictionary<ComplexValue, int>) };
            for (int index = 0; index < types.Length; index++)
            {
                var thing = (Thing)Activator.CreateInstance(typeof(RejectedThing<>).MakeGenericType(types[index]));
                var path = "/rejected" + index + "/Value";
                MyHub.TryAddThing(thing, "/", "rejected" + index);
                var error = Assert.ThrowsExactly<WrongResourceActionException>(
                    () => MyHub.SubscribeResource(path, _ => { }));
                StringAssert.Contains(error.Message, path);
                StringAssert.Contains(error.Message, "automatic observation");
                StringAssert.Contains(error.Message, "Cfet2Event");
            }
            Assert.AreEqual(0, CountObservations());
            var subscriptions = (IDictionary)MyHub.EventHub.GetType()
                .GetProperty("localSubDict", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(MyHub.EventHub);
            Assert.AreEqual(0, subscriptions.Count);
        }

        [TestMethod]
        public void WrappedAndDerivedSamplesShouldSupportObservation()
        {
            MyHub.TryAddThing(new WrappedThing(), "/", "wrapped");
            foreach (var name in new[] { "Sample", "Derived", "Config" })
            {
                using var subscription = MyHub.SubscribeResource("/wrapped/" + name, _ => { });
                Assert.IsTrue(subscription.InitialValue.IsValid);
            }
        }

        [TestMethod]
        public void ReusedSampleShouldDetectChangesAndNullTransitionsWithoutDuplicateEvents()
        {
            var thing = new WrappedThing();
            MyHub.TryAddThing(thing, "/", "wrapped");
            var values = new ConcurrentQueue<object>();
            using var subscription = MyHub.SubscribeResource("/wrapped/Sample", e => values.Enqueue(e.Sample.ObjectVal));
            Assert.IsNull(subscription.InitialValue.ObjectVal);
            Thread.Sleep(120);
            Assert.AreEqual(0, values.Count);
            thing.Sample.Context[SampleBase<int?>.KeyOfVal] = 42;
            Assert.IsTrue(SpinWait.SpinUntil(() => values.Count == 1, 2000));
            Thread.Sleep(120);
            Assert.AreEqual(1, values.Count);
            thing.Sample.Context[SampleBase<int?>.KeyOfVal] = null;
            Assert.IsTrue(SpinWait.SpinUntil(() => values.Count == 2, 2000));
            Thread.Sleep(120);
            CollectionAssert.AreEqual(new object[] { 42, null }, values.ToArray());
        }

        [TestMethod]
        public void StringChangesShouldUseContentEqualityAndSupportNull()
        {
            var thing = new StringThing();
            MyHub.TryAddThing(thing, "/", "text");
            var values = new ConcurrentQueue<object>();
            using var subscription = MyHub.SubscribeResource("/text/Value", e => values.Enqueue(e.Sample.ObjectVal));
            thing.Value = new string(new[] { 'a' });
            Thread.Sleep(150);
            Assert.AreEqual(0, values.Count);
            thing.Value = "b";
            Assert.IsTrue(SpinWait.SpinUntil(() => values.Count == 1, 2000));
            thing.Value = null;
            Assert.IsTrue(SpinWait.SpinUntil(() => values.Count == 2, 2000));
            CollectionAssert.AreEqual(new object[] { "b", null }, values.ToArray());
        }

        [TestMethod]
        public void ComplexConfigShouldRemainReadableWritableAndNativeSubscribable()
        {
            var thing = new ComplexThing();
            MyHub.TryAddThing(thing, "/", "complex");
            var resource = (Resource.ResourceConfig)MyHub.FindLocalResourceWithPath("/complex/Value");
            var replacement = new[] { 4, 5 };
            Assert.IsTrue(((ISample)resource.Set(new object[] { replacement })).IsValid);
            Assert.AreSame(replacement, ((ISample)resource.Get()).ObjectVal);
            using (var observed = MyHub.SubscribeResource("/complex/Value", _ => { }))
                Assert.AreEqual(1, CountObservations());
            var values = new ConcurrentQueue<object>();
            using var subscription = MyHub.SubscribeResource("/complex/Native", e => values.Enqueue(e.Sample.ObjectVal));
            Assert.AreEqual(0, CountObservations());
            MyHub.PublishResource("/complex/Native", replacement);
            Assert.IsTrue(SpinWait.SpinUntil(() => values.Count == 1, 2000));
            Assert.AreSame(replacement, values.ToArray()[0]);
        }

        private int CountObservations() => (int)typeof(Hub)
            .GetProperty("ObservedResourceCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(MyHub);

        public class ScalarThing<T> : Thing
        {
            [Cfet2Status] public T Value => default;
            [Cfet2Status] public T Read() => default;
        }

        public class RejectedThing<T> : Thing
        {
            [Cfet2Status] public T Value => throw new InvalidOperationException("Getter must not execute before type validation.");
        }

        public class DerivedStatus : Status<int> { public DerivedStatus() : base(1) { } }
        public class WrappedThing : Thing
        {
            [Cfet2Status] public Status<int?> Sample { get; } = new Status<int?>((int?)null);
            [Cfet2Status] public DerivedStatus Derived() => new DerivedStatus();
            [Cfet2Config] public Config<double?> Config { get; set; } = new Config<double?>((double?)null);
        }

        public class ComplexThing : Thing
        {
            [Cfet2Config] public int[] Value { get; set; } = new[] { 1 };
            [Cfet2Status, Cfet2Event] public int[] Native => Value;
        }
        public class ComplexValue { }
        public class StringThing : Thing
        {
            [Cfet2Status] public string Value { get; set; } = "a";
        }
        public struct ComplexStruct { public int[] Values; }
    }
}

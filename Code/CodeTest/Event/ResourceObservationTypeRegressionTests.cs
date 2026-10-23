#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    [DoNotParallelize]
    public class ResourceObservationTypeRegressionTests
    {
        [TestMethod]
        public void FrameworkPropertyMethodAndConfigDeclarationsKeepExistingResolution()
        {
            var types = new[] { typeof(int), typeof(int?), typeof(string), typeof(int[,]),
                typeof(List<Dictionary<string, int?[]>>), typeof(Status<int?>), typeof(Config<double?>),
                typeof(DerivedStatus), typeof(ISample), typeof(object) };
            var expected = new[] { typeof(int), typeof(int), typeof(string), typeof(int[,]),
                typeof(List<Dictionary<string, int?[]>>), typeof(int), typeof(double),
                typeof(int), null, typeof(object) };
            for (var index = 0; index < types.Length; index++)
            {
                var type = types[index];
                var fixture = typeof(Declarations<>).MakeGenericType(type);
                var resources = new ResourceStatus[] { new ResourceStatus("property", null),
                    new ResourceStatus("method", null), new ResourceConfig("config", null) };
                var members = new MemberInfo[] { fixture.GetProperty("Value"), fixture.GetMethod("Read"), fixture.GetProperty("Value") };
                for (var i = 0; i < resources.Length; i++)
                {
                    resources[i].AddImplementation(members[i]);
                    Assert.AreEqual(expected[index], ResourceObservationTypePolicy.GetValueType(resources[i]), type.ToString());
                }
            }
        }

        [TestMethod]
        public void ReplacingGetterUpdatesResolvedTypeIncludingUnsupportedAndWrappedDeclarations()
        {
            var resource = new ResourceStatus("value", null);
            Assert.IsNull(ResourceObservationTypePolicy.GetValueType(resource));
            var types = new[] { typeof(int), typeof(Status<int?>), typeof(object), typeof(string), typeof(int) };
            var expected = new[] { typeof(int), typeof(int), typeof(object), typeof(string), typeof(int) };
            for (var index = 0; index < types.Length; index++)
            {
                resource.AddImplementation(typeof(Declarations<>).MakeGenericType(types[index]).GetMethod("Read"));
                Assert.AreEqual(expected[index], ResourceObservationTypePolicy.GetValueType(resource));
            }
        }

        [TestMethod]
        public void PropertyStillTakesPrecedenceOverAddedMethod()
        {
            var resource = new ResourceStatus("value", null);
            resource.AddImplementation(typeof(Declarations<int>).GetProperty("Value"));
            Assert.AreEqual(typeof(int), ResourceObservationTypePolicy.GetValueType(resource));
            resource.AddImplementation(typeof(Declarations<string>).GetMethod("Read"));
            Assert.AreEqual(typeof(int), ResourceObservationTypePolicy.GetValueType(resource));
            resource.AddImplementation(typeof(Declarations<double?>).GetProperty("Value"));
            Assert.AreEqual(typeof(double), ResourceObservationTypePolicy.GetValueType(resource));
        }

        [TestMethod]
        public void CustomStatusAndConfigKeepDynamicInfoAndExceptionRecovery()
        {
            VerifyDynamic(new DynamicStatus());
            VerifyDynamic(new DynamicConfig());
        }

        private static void VerifyDynamic(IDynamicResource dynamic)
        {
            var resource = (ResourceBase)dynamic;
            Assert.AreEqual(typeof(int), ResourceObservationTypePolicy.GetValueType(resource));
            dynamic.Declaration = typeof(string);
            Assert.AreEqual(typeof(string), ResourceObservationTypePolicy.GetValueType(resource));
            dynamic.Fail = true;
            Assert.ThrowsExactly<InvalidOperationException>(() => ResourceObservationTypePolicy.GetValueType(resource));
            dynamic.Fail = false;
            dynamic.Declaration = typeof(Status<double?>);
            Assert.AreEqual(typeof(double), ResourceObservationTypePolicy.GetValueType(resource));
            Assert.AreEqual(4, dynamic.Reads);
        }

        [TestMethod]
        public void PollingUsesChangedGetterAndRecoversAfterInvalidReads()
        {
            using var hub = new EventHub();
            hub.Init();
            using var manager = new ResourceObservationManager(hub) { ObservationInterval = TimeSpan.FromMilliseconds(10) };
            var thing = new MutableThing();
            var parent = new ResourceThing(thing, "mutable", null);
            var resource = (ResourceStatus)parent.Resources["Number"];
            resource.Path = "/mutable/value";
            var values = new ConcurrentQueue<object>();
            using var token = hub.Subscribe(new EventFilter(resource.Path, "changed"), e => values.Enqueue(e.Sample.ObjectVal));
            var result = manager.Acquire(resource);
            Assert.AreEqual(ResourceObservationStatus.Started, result.Status);
            using var handle = result.Handle;
            resource.AddImplementation(typeof(MutableThing).GetProperty("Invalid"));
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref thing.InvalidReads) >= 3, 3000));
            Assert.AreEqual(0, values.Count);
            resource.AddImplementation(typeof(MutableThing).GetProperty("Text"));
            Assert.IsTrue(SpinWait.SpinUntil(() => values.Count == 1, 3000));
            Assert.AreEqual("recovered", values.ToArray()[0]);
        }

        [TestMethod]
        public void ReleasedObservationDoesNotReuseMetadataForNewResourceAtSamePath()
        {
            using var hub = new EventHub();
            using var manager = new ResourceObservationManager(hub) { ObservationInterval = TimeSpan.FromHours(1) };
            var first = CreateResource(typeof(int));
            var started = manager.Acquire(first, new Status<int>(1));
            Assert.AreEqual(ResourceObservationStatus.Started, started.Status);
            var shared = manager.Acquire(first, new Status<int>(99));
            Assert.AreEqual(ResourceObservationStatus.Shared, shared.Status);
            started.Handle.Dispose();
            Assert.AreEqual(1, manager.ObservedResourceCount);
            shared.Handle.Dispose();
            Assert.AreEqual(0, manager.ObservedResourceCount);
            var rejected = manager.Acquire(CreateResource(typeof(object)), new Status<int>(1));
            Assert.AreEqual(ResourceObservationStatus.Failed, rejected.Status);
            Assert.IsNull(rejected.Handle);
            var replacement = manager.Acquire(CreateResource(typeof(string)), new Status<string>("new"));
            Assert.AreEqual(ResourceObservationStatus.Started, replacement.Status);
            replacement.Handle.Dispose();
            Assert.AreEqual(0, manager.ObservedResourceCount);
        }

        private static ResourceStatus CreateResource(Type type)
        {
            var resource = new ResourceStatus("value", null) { Path = "/same/value" };
            resource.AddImplementation(typeof(Declarations<>).MakeGenericType(type).GetProperty("Value"));
            return resource;
        }

        public class Declarations<T>
        {
            [Cfet2Config] public T Value { get; set; }
            public T Read() => default;
        }
        public class DerivedStatus : Status<int?> { }
        public class MutableThing : Thing
        {
            public int InvalidReads;
            [Cfet2Status] public int Number => 1;
            public int Invalid { get { Interlocked.Increment(ref InvalidReads); throw new InvalidOperationException("temporary"); } }
            public string Text => "recovered";
        }
        private interface IDynamicResource
        {
            Type Declaration { get; set; }
            bool Fail { get; set; }
            int Reads { get; }
        }
        private static ResourceInfo DynamicInfo(IDynamicResource dynamic)
        {
            if (dynamic.Fail) throw new InvalidOperationException("temporary metadata failure");
            var info = new ResourceInfo();
            info.Implementations.Add(AccessAction.get, typeof(Declarations<>).MakeGenericType(dynamic.Declaration).GetProperty("Value"));
            return info;
        }
        private sealed class DynamicStatus : ResourceStatus, IDynamicResource
        {
            public DynamicStatus() : base("custom", null) { }
            public Type Declaration { get; set; } = typeof(int);
            public bool Fail { get; set; }
            public int Reads { get; private set; }
            public override ResourceInfo Info { get { Reads++; return DynamicInfo(this); } }
        }
        private sealed class DynamicConfig : ResourceConfig, IDynamicResource
        {
            public DynamicConfig() : base("custom", null) { }
            public Type Declaration { get; set; } = typeof(int);
            public bool Fail { get; set; }
            public int Reads { get; private set; }
            public override ResourceInfo Info { get { Reads++; return DynamicInfo(this); } }
        }
    }
}

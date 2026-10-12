#nullable disable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Sample;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize]
    public class ParameterizedSubscriptionTests : CFET2Host
    {
        private Dictionary<string, string> config;
        private Device device;
        private int Observations => (int)typeof(Hub).GetProperty("ObservedResourceCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(MyHub);
        private int Subscriptions => ((IDictionary)MyHub.EventHub.GetType().GetProperty("localSubDict", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(MyHub.EventHub)).Count;
        private static void Eventually(Func<bool> predicate) => Assert.IsTrue(SpinWait.SpinUntil(predicate, 3000));
        [TestInitialize]
        public void Init()
        {
            config = GlobalConfig.Get();
            GlobalConfig.AutoObserveList = new[] { new ObservationListRule(regex: "/p/.*") };
            GlobalConfig.AutoDenyList = new ObservationListRule[0];
            GlobalConfig.ResourceObservationIntervalMs = 20;
            HubMaster.InjectHubToModule(this);
            device = new Device();
            MyHub.TryAddThing(device, "/", "p");
        }
        [TestCleanup] public void Cleanup() { Hub.KillMaster(); GlobalConfig.Set(config); }

        [TestMethod]
        [DataRow("p/Required/1")]
        [DataRow("p/NoArgs")]
        [DataRow("./p/Required/1")]
        [DataRow("../p/Required/1")]
        [DataRow("")]
        public void RelativeLocalAddressIsRejectedBeforeReadOrSubscription(string path)
        {
            var error = Assert.ThrowsExactly<ArgumentException>(() => MyHub.SubscribeResource(path, _ => { }));
            Assert.AreEqual("resourceUri", error.ParamName);
            StringAssert.Contains(error.Message, "must start with '/'");
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(0, Subscriptions);
            Assert.AreEqual(0, Observations);
        }

        [TestMethod]
        public void ParameterGettersSkipIndependentGroupAndResidentObservationWithoutReads()
        {
            foreach (var name in new[] { "Required", "Default", "Variable", "alias" })
            {
                var result = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/p/" + name));
                Assert.AreEqual(ResourceObservationStatus.Skipped, result.Status);
                Assert.IsNull(result.Handle);
            }
            using var group = MyHub.SubscribeResources(new EventFilter("/p/(Required|Default|Variable|alias)", "changed"), _ => { });
            Assert.IsTrue(group.ObservationResults.All(r => r.Status == ResourceObservationStatus.Skipped));
            MyHub.StartThings();
            foreach (var name in new[] { "Required", "Default", "Variable", "alias" })
                Assert.AreEqual(ResourceObservationStatus.Skipped, MyHub.AutoObservationResults.Single(r => r.ResourcePath == "/p/" + name).Status);
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(1, Observations); // Only NoArgs remains eligible.
        }

        [TestMethod]
        public void ExactInitialReadUsesParametersOnceAndOnlyReceivesSameSource()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/p/Required/1", events.Enqueue);
            Assert.AreEqual(1, subscription.InitialValue.ObjectVal);
            Assert.AreEqual("/p/Required", subscription.InitialValue.Path);
            Assert.AreEqual(1, device.Reads);
            Assert.AreEqual(0, Observations);
            Assert.IsTrue(device.PublishResourceIfChanged("Required", 3, new object[] { 2 }));
            Assert.IsTrue(device.PublishResourceIfChanged("Required", 4, new object[] { 1 }));
            Eventually(() => events.Count == 1);
            Thread.Sleep(100);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("/p/Required/1", events.Single().Source);
            Assert.AreEqual(1, device.Reads);
        }

        [TestMethod]
        public void DefaultsAndEmptyParamsReadButKeepExplicitSourceUnchanged()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var defaultBase = MyHub.SubscribeResource("/p/Default", events.Enqueue);
            using var defaultDetail = MyHub.SubscribeResource("/p/Default/7", events.Enqueue);
            using var empty = MyHub.SubscribeResource("/p/Variable", events.Enqueue);
            using var values = MyHub.SubscribeResource("/p/Variable/1/2", events.Enqueue);
            Assert.AreEqual(7, defaultBase.InitialValue.ObjectVal);
            Assert.AreEqual(7, defaultDetail.InitialValue.ObjectVal);
            Assert.AreEqual("", empty.InitialValue.ObjectVal);
            Assert.AreEqual("1,2", values.InitialValue.ObjectVal);
            Assert.AreEqual(4, device.Reads);
            Assert.AreEqual(0, Observations);
            device.PublishResourceIfChanged("Default", 8); // /Default/7, not /Default.
            device.PublishResourceIfChanged("Variable", "empty");
            device.PublishResourceIfChanged("Variable", "values", new object[] { 1, 2 });
            Eventually(() => events.Count == 3);
            Thread.Sleep(80);
            CollectionAssert.AreEquivalent(new[] { "/p/Default/7", "/p/Variable", "/p/Variable/1/2" }, events.Select(e => e.Source).ToArray());
            Assert.AreEqual(4, device.Reads);
        }

        [TestMethod]
        public void InputConversionNeverRewritesSubscriptionAddress()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/p/Required/01", events.Enqueue);
            Assert.AreEqual(1, subscription.InitialValue.ObjectVal);
            MyHub.PublishResource("/p/Required/1", 2);
            MyHub.PublishResource("/p/Required/01", 3);
            Eventually(() => events.Count == 1);
            Thread.Sleep(80);
            Assert.AreEqual("/p/Required/01", events.Single().Source);
        }

        [TestMethod]
        public void ExactLiteralParametersAndResourceNamesEscapeRegexCharacters()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/p/alias/1/a.b[2]", events.Enqueue);
            Assert.AreEqual("1:a.b[2]", subscription.InitialValue.ObjectVal);
            MyHub.PublishResource("/p/alias/1/axb2", 1);
            MyHub.PublishResource("/p/alias/1/a.b[2]", 2);
            Eventually(() => events.Count == 1);
            Thread.Sleep(80);
            Assert.AreEqual("/p/alias/1/a.b[2]", events.Single().Source);
        }

        [TestMethod]
        public void RequiredDefaultAndVariableGettersCanReceiveCustomActiveEventTypes()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/p/Required/1", events.Enqueue, "alarm");
            MyHub.Publish("/p/Required/1", "alarm", 9);
            Eventually(() => events.Count == 1);
            Assert.AreEqual(0, Observations);
        }

        [TestMethod]
        public void ParameterizedComplexValuesAreNotRestrictedByPollingTypePolicy()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/p/Complex/1", events.Enqueue, "alarm");
            Assert.IsInstanceOfType<ComplexValue>(subscription.InitialValue.ObjectVal);
            MyHub.Publish("/p/Complex/1", "alarm", new ComplexValue());
            Eventually(() => events.Count == 1);
            Assert.AreEqual(1, device.Reads);
            Assert.AreEqual(0, Observations);
        }

        [TestMethod]
        public void InvalidParametersAndFailedGetLeaveNoSubscriptionsOrObservations()
        {
            foreach (var path in new[] { "/p/Required", "/p/Required/bad", "/p/Required/1/2", "/p/Default/7/8", "/p/Broken/1" })
                Assert.ThrowsExactly<BadResourceRequestException>(() => MyHub.SubscribeResource(path, _ => { }), path);
            Assert.AreEqual(0, Subscriptions);
            Assert.AreEqual(0, Observations);
            Assert.AreEqual(0, device.Reads);
        }

        [TestMethod]
        public void RegexAllAndPrefixExcludeBaseAndPrefixEvents()
        {
            var all = new ConcurrentQueue<EventArg>();
            var prefix = new ConcurrentQueue<EventArg>();
            var basis = new ConcurrentQueue<EventArg>();
            using var a = MyHub.SubscribeResources(new EventFilter("/p/Variable/.*", "changed"), all.Enqueue);
            using var p = MyHub.Subscribe(new EventFilter("/p/Variable/1/.*", "changed"), prefix.Enqueue);
            using var b = MyHub.Subscribe(new EventFilter("/p/Variable", "changed"), basis.Enqueue);
            Assert.AreEqual(0, a.ObservationResults.Count);
            foreach (var path in new[] { "/p/Variable", "/p/Variable/1", "/p/Variable/1/2", "/p/Variable/10/2" })
                MyHub.PublishResource(path, path);
            Eventually(() => all.Count == 3 && prefix.Count == 1 && basis.Count == 1);
            Thread.Sleep(80);
            Assert.AreEqual("/p/Variable/1/2", prefix.Single().Source);
            Assert.AreEqual("/p/Variable", basis.Single().Source);
            Assert.AreEqual(0, device.Reads);
            Assert.AreEqual(0, Observations);
        }

        [TestMethod]
        public void IndexGetterMetadataIsRecognizedAsParameterized()
        {
            var info = new Resource.ResourceInfo();
            info.Implementations[AccessAction.get] = typeof(Indexed).GetProperty("Item");
            Assert.IsTrue(info.HasGetterParameters);
        }

        [TestMethod]
        public void RemoteExactInitialReadUsesFullUriAndEscapesEventSource()
        {
            var resourceComm = new DummyComm();
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(resourceComm);
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("test://localhost:99/p/alias/1/a.b", events.Enqueue);
            Assert.AreEqual("1:a.b", subscription.InitialValue.ObjectVal);
            Assert.AreEqual(1, device.Reads);
            Assert.AreEqual("test://localhost:99", eventComm.LastFilter.Host);
            Assert.IsTrue(eventComm.LastFilter.Matches("/p/alias/1/a.b", "changed"));
            Assert.IsFalse(eventComm.LastFilter.Matches("/p/alias/1/axb", "changed"));
            eventComm.EmitRemoteEvent(new EventArg("/p/alias/1/axb", "changed", new Status<int>(1), "test://localhost:99"));
            eventComm.EmitRemoteEvent(new EventArg("/p/alias/1/a.b", "changed", new Status<int>(2), "test://localhost:99"));
            Eventually(() => events.Count == 1);
            Thread.Sleep(80);
            Assert.AreEqual("/p/alias/1/a.b", events.Single().Source);
            Assert.AreEqual(1, device.Reads);
            Assert.AreEqual(0, Observations);
        }

        [TestMethod]
        public void LocalCfEtUriUsesItsLiteralSourcePath()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("cfet://localhost/p/Required/1", events.Enqueue);
            Assert.AreEqual(1, subscription.InitialValue.ObjectVal);
            device.PublishResourceIfChanged("Required", 3, new object[] { 1 });
            Eventually(() => events.Count == 1);
            Assert.AreEqual("/p/Required/1", events.Single().Source);
        }

        public class Indexed { public int this[int channel] => channel; }
        public class ComplexValue { public int Value { get; set; } }
        public class Device : Thing
        {
            public int Reads;
            [Cfet2Status] public int Required(int channel) { Interlocked.Increment(ref Reads); return channel; }
            [Cfet2Status] public int Default(int channel = 7) { Interlocked.Increment(ref Reads); return channel; }
            [Cfet2Status] public string Variable(params int[] channels) { Interlocked.Increment(ref Reads); return string.Join(",", channels); }
            [Cfet2Status(Name = "alias")] public string Multiple(int channel, string name) { Interlocked.Increment(ref Reads); return channel + ":" + name; }
            [Cfet2Status] public int Broken(int channel) => throw new InvalidOperationException("Device failed.");
            [Cfet2Status] public ComplexValue Complex(int channel) { Interlocked.Increment(ref Reads); return new ComplexValue { Value = channel }; }
            [Cfet2Status] public int NoArgs => 1;
        }
    }
}

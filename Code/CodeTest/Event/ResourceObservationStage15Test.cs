using System;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Extension;
using Jtext103.CFET2.Core.Sample;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class ResourceObservationStage15Test : CFET2Host
    {
        [TestInitialize]
        public void Init()
        {
            GlobalConfig.ResourceObservationIntervalMs = 500;
            HubMaster.InjectHubToModule(this);
        }

        [TestCleanup]
        public void Clean()
        {
            Hub.KillMaster();
        }

        [TestMethod]
        public void SubscribeResourceShouldReturnInitialStatusValue()
        {
            var thing = new ObservedEventThing();
            MyHub.TryAddThing(thing, "/", "thing");

            using var subscription = MyHub.SubscribeResource("/thing/ObservedStatus", e => { });

            subscription.Token.Should().NotBeNull();
            subscription.InitialValue.ObjectVal.Should().Be(1);
            subscription.InitialValue.Path.Should().Be("/thing/ObservedStatus");
        }

        [TestMethod]
        public void SubscribeResourceShouldReturnInitialConfigValue()
        {
            var thing = new ObservedEventThing();
            MyHub.TryAddThing(thing, "/", "thing");

            using var subscription = MyHub.SubscribeResource("/thing/ObservedConfig", e => { });

            subscription.InitialValue.ObjectVal.Should().Be(2);
            subscription.InitialValue.Path.Should().Be("/thing/ObservedConfig");
        }

        [TestMethod]
        public void SubscribeResourceShouldSupportInitialValueThenCallbackUsage()
        {
            var thing = new NativeEventThing();
            ISample currentValue = null;
            MyHub.TryAddThing(thing, "/", "thing");

            using var subscription = MyHub.SubscribeResource(
                "/thing/NativeStatus",
                e => currentValue = e.Sample);

            currentValue = subscription.InitialValue;
            currentValue.ObjectVal.Should().Be(1);

            thing.PublishNativeStatus(10);
            Thread.Sleep(500);

            currentValue.ObjectVal.Should().Be(10);
        }

        [TestMethod]
        public void TrySubscribeResourceWithUriShouldForwardToSubscribeResource()
        {
            var thing = new ObservedEventThing();
            MyHub.TryAddThing(thing, "/", "thing");

            using var subscription = MyHub.TrySubscribeResourceWithUri("/thing/ObservedStatus", "changed", e => { });

            subscription.InitialValue.ObjectVal.Should().Be(1);
        }

        [TestMethod]
        public void UnmarkedStatusShouldBeObservedAndPublishChangedEvent()
        {
            var thing = new ObservedEventThing();
            EventArg received = null!;
            MyHub.TryAddThing(thing, "/", "thing");

            using var subscription = MyHub.SubscribeResource(
                "/thing/ObservedStatus",
                e => received = e);

            thing.SetObservedStatus(10);
            Thread.Sleep(1200);

            received.Should().NotBeNull();
            received.Source.Should().Be("/thing/ObservedStatus");
            received.EventType.Should().Be("changed");
            received.Sample.ObjectVal.Should().Be(10);
        }

        [TestMethod]
        public void UnmarkedConfigShouldBeObservedAndPublishChangedEvent()
        {
            var thing = new ObservedEventThing();
            EventArg received = null!;
            MyHub.TryAddThing(thing, "/", "thing");

            using var subscription = MyHub.SubscribeResource(
                "/thing/ObservedConfig",
                e => received = e);

            thing.ObservedConfig = 20;
            Thread.Sleep(1200);

            received.Should().NotBeNull();
            received.Source.Should().Be("/thing/ObservedConfig");
            received.EventType.Should().Be("changed");
            received.Sample.ObjectVal.Should().Be(20);
        }

        [TestMethod]
        public void SubscribeResourceShouldNotPublishInitialValueAsChangedEvent()
        {
            var thing = new ObservedEventThing();
            var receivedCount = 0;
            MyHub.TryAddThing(thing, "/", "thing");

            using var subscription = MyHub.SubscribeResource(
                "/thing/ObservedStatus",
                e => Interlocked.Increment(ref receivedCount));

            Thread.Sleep(1200);

            receivedCount.Should().Be(0);
            subscription.InitialValue.ObjectVal.Should().Be(1);
        }

        [TestMethod]
        public void NativeEventResourceShouldNotBeObservedByCore()
        {
            var thing = new NativeEventThing();
            var receivedCount = 0;
            MyHub.TryAddThing(thing, "/", "thing");

            using var subscription = MyHub.SubscribeResource(
                "/thing/NativeStatus",
                e => Interlocked.Increment(ref receivedCount));

            thing.SetNativeStatusWithoutPublish(10);
            Thread.Sleep(1200);

            receivedCount.Should().Be(0);

            thing.PublishNativeStatus(11);
            Thread.Sleep(500);

            receivedCount.Should().Be(1);
        }

        [TestMethod]
        public void DisposedResourceSubscriptionShouldStopReceivingObservedEvents()
        {
            var thing = new ObservedEventThing();
            var receivedCount = 0;
            MyHub.TryAddThing(thing, "/", "thing");

            using (MyHub.SubscribeResource(
                "/thing/ObservedStatus",
                e => Interlocked.Increment(ref receivedCount)))
            {
            }

            thing.SetObservedStatus(10);
            Thread.Sleep(1200);

            receivedCount.Should().Be(0);
        }

        [TestMethod]
        public void MethodShouldNotSupportResourceSubscribe()
        {
            MyHub.TryAddThing(new ObservedEventThing(), "/", "thing");

            Action act = () => MyHub.SubscribeResource("/thing/Method", e => { });

            act.Should().Throw<WrongResourceActionException>()
                .WithMessage("*SubscribeResource only supports Status and Config resources*");
        }

        [TestMethod]
        public void UnmarkedResourceShouldOnlySupportChangedObservation()
        {
            MyHub.TryAddThing(new ObservedEventThing(), "/", "thing");

            Action act = () => MyHub.SubscribeResource("/thing/ObservedStatus", e => { }, "alarm");

            act.Should().Throw<WrongResourceActionException>()
                .WithMessage("*Observed resources without Cfet2Event only support changed event*");
        }

        [TestMethod]
        public void MultipleSubscribersForSameResourceShouldShareOneObservation()
        {
            var thing = new ObservedEventThing();
            MyHub.TryAddThing(thing, "/", "thing");

            using var first = MyHub.SubscribeResource("/thing/ObservedStatus", e => { });
            using var second = MyHub.SubscribeResource("/thing/ObservedStatus", e => { });

            getObservedResourceCount().Should().Be(1);
        }

        [TestMethod]
        public void ObservationIntervalShouldUseGlobalConfigValue()
        {
            Hub.KillMaster();
            GlobalConfig.ResourceObservationIntervalMs = 100;
            HubMaster.InjectHubToModule(this);
            var thing = new ObservedEventThing();
            EventArg received = null!;
            MyHub.TryAddThing(thing, "/", "thing");

            using var subscription = MyHub.SubscribeResource(
                "/thing/ObservedStatus",
                e => received = e);

            thing.SetObservedStatus(10);
            Thread.Sleep(350);

            received.Should().NotBeNull();
            received.Sample.ObjectVal.Should().Be(10);
        }

        [TestMethod]
        public void SubscribeResourceShouldThrowAndCancelSubscriptionWhenInitialGetFails()
        {
            var thing = new InitialGetFailureThing();
            var receivedCount = 0;
            MyHub.TryAddThing(thing, "/", "thing");

            Action act = () => MyHub.SubscribeResource(
                "/thing/FailingStatus",
                e => Interlocked.Increment(ref receivedCount));

            act.Should().Throw<BadResourceRequestException>()
                .WithMessage("*SubscribeResource initial get failed*");

            MyHub.Publish("/thing/FailingStatus", "changed", 10);
            Thread.Sleep(500);

            receivedCount.Should().Be(0);
            getObservedResourceCount().Should().Be(0);
        }

        [TestMethod]
        public void RemoteSubscribeResourceShouldReturnInitialValueAndReceiveRemoteEvents()
        {
            var thing = new ObservedEventThing();
            var resourceComm = new DummyComm();
            var eventComm = new DummyEventCommunicationModule();
            EventArg received = null;
            MyHub.TryAddThing(thing, "/", "thing");
            MyHub.TryAddCommunicationModule(resourceComm);
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();

            using var subscription = MyHub.SubscribeResource(
                "test://localhost:99/thing/ObservedStatus",
                e => received = e);

            subscription.InitialValue.ObjectVal.Should().Be(1);
            subscription.InitialValue.IsRemote.Should().BeTrue();
            subscription.Token.IsRemote.Should().BeTrue();
            eventComm.SubscribeCount.Should().Be(1);
            eventComm.LastFilter.Host.Should().Be("test://localhost:99");
            eventComm.LastFilter.SourcesAndTypes[0].Source.Should().Be("/thing/ObservedStatus");

            eventComm.EmitRemoteEvent(new EventArg(
                "/thing/ObservedStatus",
                "changed",
                10.ToStatus(),
                "test://localhost:99"));
            Thread.Sleep(500);

            received.Should().NotBeNull();
            received.Sample.ObjectVal.Should().Be(10);
        }

        [TestMethod]
        public void RemoteSubscribeResourceShouldThrowWhenEventCommunicationModuleIsMissing()
        {
            var thing = new ObservedEventThing();
            var resourceComm = new DummyComm();
            MyHub.TryAddThing(thing, "/", "thing");
            MyHub.TryAddCommunicationModule(resourceComm);

            Action act = () => MyHub.SubscribeResource("dummy://localhost:99/thing/ObservedStatus", e => { });

            act.Should().Throw<ProtocolNotSuportedException>()
                .WithMessage("*event is not supported*");
        }

        [TestMethod]
        public void RemoteSubscribeResourceShouldThrowWhenEventSubscribeFails()
        {
            var thing = new ObservedEventThing();
            var resourceComm = new DummyComm();
            var eventComm = new DummyEventCommunicationModule { ThrowOnSubscribe = true };
            EventArg received = null;
            MyHub.TryAddThing(thing, "/", "thing");
            MyHub.TryAddCommunicationModule(resourceComm);
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();

            Action act = () => MyHub.SubscribeResource(
                "test://localhost:99/thing/ObservedStatus",
                e => received = e);

            act.Should().Throw<BadResourceRequestException>()
                .WithMessage("*SubscribeResource event subscribe failed*");
            eventComm.SubscribeCount.Should().Be(1);
            eventComm.SubscribedTokenCount.Should().Be(0);

            eventComm.ThrowOnSubscribe = false;
            eventComm.EmitRemoteEvent(new EventArg(
                "/thing/ObservedStatus",
                "changed",
                10.ToStatus(),
                "test://localhost:99"));
            Thread.Sleep(500);

            received.Should().BeNull();
        }

        private int getObservedResourceCount()
        {
            var eventHub = MyHub.EventHub;
            var managerField = typeof(EventHub).GetField("resourceObservationManager", BindingFlags.Instance | BindingFlags.NonPublic);
            var manager = managerField.GetValue(eventHub);
            var countProperty = manager.GetType().GetProperty("ObservedResourceCount", BindingFlags.Instance | BindingFlags.NonPublic);
            return (int)countProperty.GetValue(manager);
        }

        private class ObservedEventThing : Thing
        {
            [Attributes.Cfet2Status]
            public int ObservedStatus { get; private set; } = 1;

            [Attributes.Cfet2Config]
            public int ObservedConfig { get; set; } = 2;

            [Attributes.Cfet2Method]
            public void Method()
            {
            }

            public void SetObservedStatus(int value)
            {
                ObservedStatus = value;
            }
        }

        private class NativeEventThing : Thing
        {
            [Attributes.Cfet2Status]
            [Attributes.Cfet2Event]
            public int NativeStatus { get; private set; } = 1;

            public void SetNativeStatusWithoutPublish(int value)
            {
                NativeStatus = value;
            }

            public void PublishNativeStatus(int value)
            {
                NativeStatus = value;
                MyHub.PublishResource(GetPathFor(nameof(NativeStatus)), value);
            }
        }

        private class InitialGetFailureThing : Thing
        {
            [Attributes.Cfet2Status]
            public int FailingStatus
            {
                get
                {
                    throw new InvalidOperationException("initial get failed");
                }
            }
        }
    }
}

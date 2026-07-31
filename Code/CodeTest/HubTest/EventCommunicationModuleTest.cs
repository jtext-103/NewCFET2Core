using System;
using FluentAssertions;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.HubTest
{
    [TestClass]
    public class EventCommunicationModuleTest : CFET2Host
    {
        [TestInitialize]
        public void Init()
        {
            HubMaster.InjectHubToModule(this);
        }

        [TestCleanup]
        public void Clean()
        {
            Hub.KillMaster();
        }

        [TestMethod]
        public void EventCommunicationModuleShouldBeRegisteredInjectedAndStartedOnce()
        {
            var eventComm = new DummyEventCommunicationModule();

            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();

            eventComm.MyHub.Should().NotBeNull();
            eventComm.StartCount.Should().Be(1);
        }

        [TestMethod]
        public void EventCommunicationModuleShouldNotOverrideResourceCommunicationModuleWithSameProtocol()
        {
            var resourceComm = new DummyComm();
            var eventComm = new DummyEventCommunicationModule();

            MyHub.TryAddCommunicationModule(resourceComm);
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.TryAddThing(new TestThingStatus(), "/", "thing1");
            MyHub.StartCommunication();

            var request = MyHub.TryGetResourceSampleWithUri("test://localhost:99/thing1/StatusP");
            request.ObjectVal.Should().Be(10);

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "test://localhost:99"),
                e => { });

            token.Should().NotBeNull();
            token.IsRemote.Should().BeTrue();
            eventComm.SubscribeCount.Should().Be(1);
        }

        [TestMethod]
        public void EventCommunicationModuleShouldRegisterEventCallbackWhenCommunicationStarts()
        {
            var eventComm = new DummyEventCommunicationModule();

            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();

            eventComm.LastHandler.Should().NotBeNull();
        }

        [TestMethod]
        public void RemoteSubscribeShouldNotPassBusinessHandlerToEventCommunicationModule()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var businessHandlerCalled = false;
            Action<EventArg> businessHandler = e => businessHandlerCalled = true;

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                businessHandler);

            token.Should().NotBeNull();
            eventComm.SubscribeCount.Should().Be(1);
            eventComm.LastToken.Should().Be(token);
            eventComm.LastFilter.Host.Should().Be("eventdummy://remote-host:1234");
            eventComm.LastHandler.Should().NotBeSameAs(businessHandler);
            businessHandlerCalled.Should().BeFalse();
        }

        [TestMethod]
        public void DummyEventCommunicationModuleShouldInvokeRegisteredRemoteEventCallback()
        {
            var eventComm = new DummyEventCommunicationModule();
            var callbackCount = 0;
            EventArg receivedEvent = null;
            var remoteEvent = new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234");

            eventComm.RegisterEventCallback(e =>
            {
                callbackCount++;
                receivedEvent = e;
            });

            eventComm.EmitRemoteEvent(remoteEvent);

            callbackCount.Should().Be(1);
            receivedEvent.Should().BeSameAs(remoteEvent);
        }

        [TestMethod]
        public void DummyEventCommunicationModuleShouldNotInvokeCallbackAfterDispose()
        {
            var eventComm = new DummyEventCommunicationModule();
            var callbackCount = 0;
            eventComm.RegisterEventCallback(e => callbackCount++);

            eventComm.Dispose();
            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));

            callbackCount.Should().Be(0);
            eventComm.LastHandler.Should().BeNull();
        }

        [TestMethod]
        public void DuplicateEventProtocolRegistrationShouldKeepFirstModule()
        {
            var firstEventComm = new DummyEventCommunicationModule();
            var secondEventComm = new DummyEventCommunicationModule();

            MyHub.TryAddCommunicationModule(firstEventComm);
            MyHub.TryAddCommunicationModule(secondEventComm);

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => { });

            token.Should().NotBeNull();
            firstEventComm.SubscribeCount.Should().Be(1);
            secondEventComm.SubscribeCount.Should().Be(0);
        }

        [TestMethod]
        public void EventFilterShouldResolveRemoteHostFromEventCommunicationModule()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);

            var filter = new EventFilter("eventdummy://remote-host:1234/thing1/StatusP", "changed");

            filter.Host.Should().Be("eventdummy://remote-host:1234");
            filter.SourcesAndTypes[0].Source.Should().Be("/thing1/StatusP");
        }

        [TestMethod]
        public void RemoteEventTokenDisposeShouldUnsubscribeThroughEventCommunicationModule()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => { });

            token.Dispose();

            eventComm.SubscribeCount.Should().Be(1);
            eventComm.UnsubscribeCount.Should().Be(1);
            eventComm.LastToken.Should().Be(token);
        }

        [TestMethod]
        public void EventCommunicationModuleShouldBeDisposedWhenHubMasterIsKilled()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);

            Hub.KillMaster();

            eventComm.DisposeCount.Should().Be(1);
            HubMaster.InjectHubToModule(this);
        }
    }
}

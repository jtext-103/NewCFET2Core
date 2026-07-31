using System.Threading;
using FluentAssertions;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class EventHubStage8Test : CFET2Host
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
        public void StartCommunicationShouldRegisterRemoteCallbackBeforeEventModuleStarts()
        {
            var eventComm = new StartEmittingEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            var remoteCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));

            MyHub.StartCommunication();
            Thread.Sleep(300);

            remoteCount.Should().Be(1);
        }

        [TestMethod]
        public void RemoteSubscribeShouldSaveSubscriberBeforeCommunicationModuleCanEmit()
        {
            var eventComm = new SubscribeEmittingEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var remoteCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));
            Thread.Sleep(300);

            remoteCount.Should().Be(1);
        }

        [TestMethod]
        public void SharedRemoteConnectionShouldDeliverMatchingEventToAllSubscribers()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var firstCount = 0;
            var secondCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref firstCount));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref secondCount));

            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            Thread.Sleep(300);

            firstCount.Should().Be(1);
            secondCount.Should().Be(1);
            eventComm.SubscribeCount.Should().Be(2);
        }

        private class StartEmittingEventCommunicationModule : DummyEventCommunicationModule
        {
            public override void Start()
            {
                base.Start();
                EmitRemoteEvent(new EventArg(
                    "/thing1/StatusP",
                    "changed",
                    null,
                    "eventdummy://remote-host:1234"));
            }
        }

        private class SubscribeEmittingEventCommunicationModule : DummyEventCommunicationModule
        {
            public override void Subscribe(Token token, EventFilter filter)
            {
                base.Subscribe(token, filter);
                EmitRemoteEvent(new EventArg(
                    "/thing1/StatusP",
                    "changed",
                    null,
                    "eventdummy://remote-host:1234"));
                Thread.Sleep(100);
            }
        }
    }
}

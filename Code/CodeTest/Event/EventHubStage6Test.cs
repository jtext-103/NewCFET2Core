using System;
using System.Threading;
using FluentAssertions;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class EventHubStage6Test : CFET2Host
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
        public void LocalPublishShouldOnlyTriggerLocalSubscribers()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var localCount = 0;
            var remoteCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => Interlocked.Increment(ref localCount));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));

            MyHub.EventHub.Publish("/thing1/StatusP", "changed", 1);
            Thread.Sleep(300);

            localCount.Should().Be(1);
            remoteCount.Should().Be(0);
        }

        [TestMethod]
        public void RemoteEventShouldOnlyTriggerRemoteSubscribers()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var localCount = 0;
            var remoteCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => Interlocked.Increment(ref localCount));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));

            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            Thread.Sleep(300);

            localCount.Should().Be(0);
            remoteCount.Should().Be(1);
        }

        [TestMethod]
        public void LocalAndRemoteSubscribersWithSameSourceAndEventTypeShouldBeIndependent()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var localCount = 0;
            var remoteCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => Interlocked.Increment(ref localCount));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));

            MyHub.EventHub.Publish("/thing1/StatusP", "changed", 1);
            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            Thread.Sleep(500);

            localCount.Should().Be(1);
            remoteCount.Should().Be(1);
        }

        [TestMethod]
        public void RemoteTokenDisposeShouldRemoveRemoteSubscriber()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var remoteCount = 0;

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));

            token.Dispose();
            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            Thread.Sleep(300);

            remoteCount.Should().Be(0);
            eventComm.UnsubscribeCount.Should().Be(1);
        }

        [TestMethod]
        public void FailedRemoteSubscribeShouldNotLeaveHalfRegisteredSubscriber()
        {
            var eventComm = new DummyEventCommunicationModule { ThrowOnSubscribe = true };
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var remoteCount = 0;

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));

            token.Should().NotBeNull();
            eventComm.SubscribeCount.Should().Be(1);
            eventComm.SubscribedTokenCount.Should().Be(0);

            eventComm.ThrowOnSubscribe = false;
            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            Thread.Sleep(300);

            remoteCount.Should().Be(0);
        }
    }
}

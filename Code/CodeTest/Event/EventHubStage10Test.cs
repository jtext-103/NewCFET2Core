using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class EventHubStage10Test : CFET2Host
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

        public static IEnumerable<object[]> ConnectionEventTypes
        {
            get
            {
                yield return new object[] { ConnectionStatusEvent.Disconnected };
                yield return new object[] { ConnectionStatusEvent.Reconnecting };
                yield return new object[] { ConnectionStatusEvent.Connected };
                yield return new object[] { ConnectionStatusEvent.Failed };
            }
        }

        [TestMethod]
        [DynamicData(nameof(ConnectionEventTypes))]
        public void RemoteConnectionStatusEventShouldReachBusinessSubscriber(string eventType)
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            EventArg received = null;
            var timestamp = new DateTime(2026, 5, 22, 1, 2, 3, DateTimeKind.Utc);

            MyHub.EventHub.Subscribe(
                new EventFilter(ConnectionStatusEvent.Source, eventType, 0, "eventdummy://remote-host:1234"),
                e => received = e);

            eventComm.EmitRemoteEvent(ConnectionStatusEvent.Create(
                "EVENTDUMMY://REMOTE-HOST:1234/",
                "eventdummy",
                eventType,
                "network changed",
                2,
                timestamp));
            Thread.Sleep(300);

            received.Should().NotBeNull();
            received.Source.Should().Be(ConnectionStatusEvent.Source);
            received.EventType.Should().Be(eventType);
            received.Host.Should().Be("eventdummy://remote-host:1234");

            var payload = received.Sample.GetVal<ConnectionStatusPayload>();
            payload.Host.Should().Be("eventdummy://remote-host:1234");
            payload.Protocol.Should().Be("eventdummy");
            payload.Reason.Should().Be("network changed");
            payload.Timestamp.Should().Be(timestamp);
            payload.RetryCount.Should().Be(2);
        }

        [TestMethod]
        public void ConnectionStatusEventShouldKeepRemoteHostIsolation()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var hostACount = 0;
            var hostBCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter(ConnectionStatusEvent.Source, ConnectionStatusEvent.Disconnected, 0, "eventdummy://host-a:1234"),
                e => Interlocked.Increment(ref hostACount));
            MyHub.EventHub.Subscribe(
                new EventFilter(ConnectionStatusEvent.Source, ConnectionStatusEvent.Disconnected, 0, "eventdummy://host-b:1234"),
                e => Interlocked.Increment(ref hostBCount));

            eventComm.EmitRemoteEvent(ConnectionStatusEvent.Create(
                "eventdummy://host-a:1234",
                "eventdummy",
                ConnectionStatusEvent.Disconnected,
                "closed",
                0));
            Thread.Sleep(300);

            hostACount.Should().Be(1);
            hostBCount.Should().Be(0);
        }

        [TestMethod]
        public void LocalPublishConnectionStatusShouldNotTriggerRemoteSubscribers()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var localCount = 0;
            var remoteCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter(ConnectionStatusEvent.Source, ConnectionStatusEvent.Disconnected),
                e => Interlocked.Increment(ref localCount));
            MyHub.EventHub.Subscribe(
                new EventFilter(ConnectionStatusEvent.Source, ConnectionStatusEvent.Disconnected, 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));

            MyHub.EventHub.Publish(
                ConnectionStatusEvent.Source,
                ConnectionStatusEvent.Disconnected,
                new ConnectionStatusPayload(
                    "eventdummy://remote-host:1234",
                    "eventdummy",
                    "local publish should not be remote",
                    DateTime.UtcNow,
                    0));
            Thread.Sleep(300);

            localCount.Should().Be(1);
            remoteCount.Should().Be(0);
        }
    }
}

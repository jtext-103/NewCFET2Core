using System.Threading;
using FluentAssertions;
using Jtext103.CFET2.Core.Communication;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class EventHubStage7Test : CFET2Host
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
        public void SameRemoteEventShouldOnlyTriggerSubscriberForMatchingHost()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var hostACount = 0;
            var hostBCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://host-a:1234"),
                e => Interlocked.Increment(ref hostACount));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://host-b:1234"),
                e => Interlocked.Increment(ref hostBCount));

            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://host-a:1234"));
            Thread.Sleep(300);

            hostACount.Should().Be(1);
            hostBCount.Should().Be(0);
        }

        [TestMethod]
        public void RemoteEventWithoutHostShouldNotTriggerRemoteSubscribers()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var remoteCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));

            eventComm.EmitRemoteEvent(new EventArg("/thing1/StatusP", "changed", null));
            Thread.Sleep(300);

            remoteCount.Should().Be(0);
        }

        [TestMethod]
        public void RemoteEventHostShouldBeNormalizedBeforeMatchingSubscribers()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.TryAddCommunicationModule(new HttpEventCommunicationModule());
            MyHub.StartCommunication();
            var remoteCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "http://remote-host:80"),
                e => Interlocked.Increment(ref remoteCount));

            eventComm.EmitRemoteEvent(new EventArg
            {
                Source = "/thing1/StatusP",
                EventType = "changed",
                Host = "HTTP://REMOTE-HOST/"
            });
            Thread.Sleep(300);

            remoteCount.Should().Be(1);
        }

        [TestMethod]
        public void RemoteSubscribeShouldNormalizeDirectlyAssignedFilterHost()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            var filter = new EventFilter("/thing1/StatusP", "changed")
            {
                Host = "EVENTDUMMY://REMOTE-HOST:1234/"
            };

            var token = MyHub.EventHub.Subscribe(filter, e => { });

            token.Host.Should().Be("eventdummy://remote-host:1234");
            filter.Host.Should().Be("eventdummy://remote-host:1234");
            eventComm.LastFilter.Host.Should().Be("eventdummy://remote-host:1234");
        }

        private class HttpEventCommunicationModule : IEventCommunicationModule
        {
            public override Config.EventTransport? Transport => Config.EventTransport.WebSocket;
            public override string[] ProtocolNames => new[] { "http" };

            public override void Dispose()
            {
            }

            public override string GetFullHost(string protocol, string hostname, int port)
            {
                return $"{protocol}://{hostname}:{port}";
            }

            public override void RegisterEventCallback(System.Action<EventArg> hander)
            {
            }

            public override void Subscribe(Token token, EventFilter filter)
            {
            }

            public override void UnSubScribe(Token token)
            {
            }
        }
    }
}

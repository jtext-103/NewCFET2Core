using FluentAssertions;
using Jtext103.CFET2.Core.Communication;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class EventModelStage4Test : CFET2Host
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
        public void LocalEventArgShouldHaveEmptyHost()
        {
            var eventArg = new EventArg("/thing/status", "changed", null);

            eventArg.Host.Should().BeEmpty();
        }

        [TestMethod]
        public void RemoteEventArgShouldKeepSourceHost()
        {
            var eventArg = new EventArg(
                "/thing/status",
                "changed",
                null,
                "eventdummy://remote-host:1234");

            eventArg.Host.Should().Be("eventdummy://remote-host:1234");
        }

        [TestMethod]
        public void RemoteTokenShouldExposeProtocolAndHost()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing/status", "changed", 0, "eventdummy://remote-host:1234"),
                e => { });

            var hostProperty = typeof(Token).GetProperty("Host");
            hostProperty.Should().NotBeNull("stage 4 requires remote tokens to expose the normalized host");
            token.Protocol.Should().Be("eventdummy");
            hostProperty.GetValue(token).Should().Be("eventdummy://remote-host:1234");
        }

        [TestMethod]
        public void RemoteFilterShouldNormalizeHostToProtocolHostPort()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);

            var filter = new EventFilter("EVENTDUMMY://REMOTE-HOST:1234/thing/status", "changed");

            filter.Host.Should().Be("eventdummy://remote-host:1234");
            filter.SourcesAndTypes[0].Source.Should().Be("/thing/status");
        }

        [TestMethod]
        public void RemoteFilterExplicitHostShouldNormalizeHostToProtocolHostPort()
        {
            var filter = new EventFilter(
                "/thing/status",
                "changed",
                0,
                "EVENTDUMMY://REMOTE-HOST:1234/");

            filter.Host.Should().Be("eventdummy://remote-host:1234");
        }

        [TestMethod]
        public void RemoteFilterExplicitHostShouldNormalizeDefaultHttpPort()
        {
            var filter = new EventFilter(
                "/thing/status",
                "changed",
                0,
                "HTTP://REMOTE-HOST/");

            filter.Host.Should().Be("http://remote-host:80");
        }

        [TestMethod]
        public void RemoteFilterUriShouldNormalizeDefaultHttpPort()
        {
            MyHub.TryAddCommunicationModule(new HttpEventCommunicationModule());

            var filter = new EventFilter("HTTP://REMOTE-HOST/thing/status", "changed");

            filter.Host.Should().Be("http://remote-host:80");
            filter.SourcesAndTypes[0].Source.Should().Be("/thing/status");
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

            public override void RegisterEventCallback(Action<EventArg> hander)
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

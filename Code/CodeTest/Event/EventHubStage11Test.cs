using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class EventHubStage11Test : CFET2Host
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
        public void LocalExactSubscriptionShouldBeIndexedInRouteTable()
        {
            var exactCount = 0;
            var otherCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => Interlocked.Increment(ref exactCount));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing2/StatusP", "changed"),
                e => Interlocked.Increment(ref otherCount));

            GetRouteTable(MyHub.EventHub, "localRouteTable").Count.Should().Be(2);

            MyHub.EventHub.Publish("/thing1/StatusP", "changed", 1);
            Thread.Sleep(300);

            exactCount.Should().Be(1);
            otherCount.Should().Be(0);
        }

        [TestMethod]
        public void RegexSubscriptionShouldRemainInFallbackAndKeepMatchingBehavior()
        {
            var regexCount = 0;
            var exactCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter(@"/thing(\d+)/StatusP", "changed"),
                e => Interlocked.Increment(ref regexCount));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => Interlocked.Increment(ref exactCount));

            GetFallbackRoutes(MyHub.EventHub, "localFallbackRoutes").Count.Should().Be(1);
            GetRouteTable(MyHub.EventHub, "localRouteTable").Count.Should().Be(1);

            MyHub.EventHub.Publish("/thing1/StatusP", "changed", 1);
            Thread.Sleep(300);

            regexCount.Should().Be(1);
            exactCount.Should().Be(1);
        }

        [TestMethod]
        public void RemoteExactSubscriptionShouldBeIndexedByHostSourceAndEventType()
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

            GetRouteTable(MyHub.EventHub, "remoteRouteTable").Count.Should().Be(2);

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
        public void TokenDisposeShouldRemoveSubscriberFromRouteTable()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var localToken = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => { });
            var remoteToken = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => { });

            localToken.Dispose();
            remoteToken.Dispose();

            GetRouteTable(MyHub.EventHub, "localRouteTable").Should().BeEmpty();
            GetRouteTable(MyHub.EventHub, "remoteRouteTable").Should().BeEmpty();
        }

        [TestMethod]
        public void ManyExactSubscriptionsShouldDispatchOnlyMatchingSubscriber()
        {
            var matchedCount = 0;
            var unmatchedCount = 0;

            for (int i = 0; i < 200; i++)
            {
                var source = "/thing" + i + "/StatusP";
                MyHub.EventHub.Subscribe(
                    new EventFilter(source, "changed"),
                    e => Interlocked.Increment(ref unmatchedCount));
            }

            MyHub.EventHub.Subscribe(
                new EventFilter("/target/StatusP", "changed"),
                e => Interlocked.Increment(ref matchedCount));

            MyHub.EventHub.Publish("/target/StatusP", "changed", 1);
            Thread.Sleep(300);

            matchedCount.Should().Be(1);
            unmatchedCount.Should().Be(0);
        }

        private static Dictionary<string, List<Guid>> GetRouteTable(EventHub hub, string propertyName)
        {
            return (Dictionary<string, List<Guid>>)typeof(EventHub)
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(hub);
        }

        private static List<Guid> GetFallbackRoutes(EventHub hub, string propertyName)
        {
            return (List<Guid>)typeof(EventHub)
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(hub);
        }
    }
}

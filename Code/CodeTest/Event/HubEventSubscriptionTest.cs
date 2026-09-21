using System.Threading;
using FluentAssertions;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class HubEventSubscriptionTest : CFET2Host
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
        public void HubSubscribeShouldSubscribeNormalEvent()
        {
            EventArg received = null;

            using var token = MyHub.Subscribe(
                new EventFilter("/thing/status", "changed"),
                e => received = e);

            MyHub.Publish("/thing/status", "changed", 10);
            Thread.Sleep(500);

            token.Should().NotBeNull();
            received.Should().NotBeNull();
            received.Source.Should().Be("/thing/status");
            received.EventType.Should().Be("changed");
            received.Sample.ObjectVal.Should().Be(10);
        }

        [TestMethod]
        public void HubUnsubscribeShouldStopNormalEventSubscription()
        {
            var receivedCount = 0;
            var token = MyHub.Subscribe(
                new EventFilter("/thing/status", "changed"),
                e => Interlocked.Increment(ref receivedCount));

            MyHub.Unsubscribe(token);
            MyHub.Publish("/thing/status", "changed", 10);
            Thread.Sleep(500);

            receivedCount.Should().Be(0);
        }

        [TestMethod]
        public void HubPublishEventArgShouldPublishNormalEvent()
        {
            EventArg received = null;

            using var token = MyHub.Subscribe(
                new EventFilter("/thing/status", "changed"),
                e => received = e);

            MyHub.Publish(new EventArg("/thing/status", "changed", new Status<int>(20)));
            Thread.Sleep(500);

            received.Should().NotBeNull();
            received.Source.Should().Be("/thing/status");
            received.EventType.Should().Be("changed");
            received.Sample.ObjectVal.Should().Be(20);
        }

        [TestMethod]
        public void HubPublishWithRelativeSourceShouldNotAutomaticallyScopeToPublishingThing()
        {
            var firstThing = new PublishingThing();
            var secondThing = new PublishingThing();
            MyHub.TryAddThing(firstThing, "/things", "first");
            MyHub.TryAddThing(secondThing, "/things", "second");
            var receivedCount = 0;
            EventArg lastReceived = null;

            using var token = MyHub.Subscribe(
                new EventFilter("Status", "changed"),
                e =>
                {
                    lastReceived = e;
                    Interlocked.Increment(ref receivedCount);
                });

            firstThing.PublishRelativeStatus(1);
            secondThing.PublishRelativeStatus(2);
            Thread.Sleep(500);

            receivedCount.Should().Be(2);
            lastReceived.Should().NotBeNull();
            lastReceived.Source.Should().Be("Status");
            lastReceived.Sample.ObjectVal.Should().Be(2);
        }

        [TestMethod]
        public void HubPublishWithThingPathShouldKeepMultipleThingInstancesIsolated()
        {
            var firstThing = new PublishingThing();
            var secondThing = new PublishingThing();
            MyHub.TryAddThing(firstThing, "/things", "first");
            MyHub.TryAddThing(secondThing, "/things", "second");
            EventArg firstReceived = null;
            EventArg secondReceived = null;

            using var firstToken = MyHub.Subscribe(
                new EventFilter("/things/first/Status", "changed"),
                e => firstReceived = e);
            using var secondToken = MyHub.Subscribe(
                new EventFilter("/things/second/Status", "changed"),
                e => secondReceived = e);

            firstThing.PublishScopedStatus(1);
            secondThing.PublishScopedStatus(2);
            Thread.Sleep(500);

            firstReceived.Should().NotBeNull();
            firstReceived.Source.Should().Be("/things/first/Status");
            firstReceived.Sample.ObjectVal.Should().Be(1);
            secondReceived.Should().NotBeNull();
            secondReceived.Source.Should().Be("/things/second/Status");
            secondReceived.Sample.ObjectVal.Should().Be(2);
        }

        [TestMethod]
        public void GetPathForShouldUseDeclaredStatusAndConfigNames()
        {
            var thing = new RenamedResourceThing();
            MyHub.TryAddThing(thing, "/devices", "meter");

            thing.GetPathFor(nameof(RenamedResourceThing.InternalTemperature))
                .Should().Be("/devices/meter/temperature");
            thing.GetPathFor(nameof(RenamedResourceThing.InternalSetPoint))
                .Should().Be("/devices/meter/setPoint");
        }

        [TestMethod]
        public void HubPublishWithRenamedStatusMemberShouldPublishDeclaredResourcePath()
        {
            var thing = new RenamedResourceThing();
            MyHub.TryAddThing(thing, "/devices", "meter");
            EventArg received = null;

            using var token = MyHub.Subscribe(
                new EventFilter("/devices/meter/temperature", "changed"),
                e => received = e);

            thing.PublishTemperature(25);
            Thread.Sleep(500);

            received.Should().NotBeNull();
            received.Source.Should().Be("/devices/meter/temperature");
            received.EventType.Should().Be("changed");
            received.Sample.ObjectVal.Should().Be(25);
        }

        private class PublishingThing : Thing
        {
            [Cfet2Status]
            public int Status { get; private set; }

            public void PublishRelativeStatus(int value)
            {
                Status = value;
                MyHub.Publish(nameof(Status), "changed", value);
            }

            public void PublishScopedStatus(int value)
            {
                Status = value;
                MyHub.PublishResource(GetPathFor(nameof(Status)), value);
            }
        }

        private class RenamedResourceThing : Thing
        {
            [Cfet2Status(Name = "temperature")]
            public int InternalTemperature { get; private set; }

            [Cfet2Config(Name = "setPoint")]
            public int InternalSetPoint { get; set; }

            public void PublishTemperature(int value)
            {
                InternalTemperature = value;
                MyHub.PublishResource(GetPathFor(nameof(InternalTemperature)), value);
            }
        }
    }
}

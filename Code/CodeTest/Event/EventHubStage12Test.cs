using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class EventHubStage12Test : CFET2Host
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
        public void LocalHandlerExceptionShouldNotBlockOtherSubscribersOrLaterEvents()
        {
            var handledCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => throw new InvalidOperationException("expected local handler failure"));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => Interlocked.Increment(ref handledCount));

            MyHub.EventHub.Publish("/thing1/StatusP", "changed", 1);
            MyHub.EventHub.Publish("/thing1/StatusP", "changed", 2);
            Thread.Sleep(500);

            handledCount.Should().Be(2);
        }

        [TestMethod]
        public void RemoteHandlerExceptionShouldNotBlockOtherSubscribersOrLaterEvents()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var handledCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => throw new InvalidOperationException("expected remote handler failure"));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref handledCount));

            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            Thread.Sleep(500);

            handledCount.Should().Be(2);
        }

        [TestMethod]
        public void LocalEventLoopShouldStayAliveWhenPublishingAndUnsubscribingConcurrently()
        {
            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => { });

            var publishTask = Task.Run(() =>
            {
                for (int i = 0; i < 300; i++)
                {
                    MyHub.EventHub.Publish("/thing1/StatusP", "changed", i);
                }
            });

            Thread.Sleep(10);
            Action dispose = () => token.Dispose();

            dispose.Should().NotThrow();
            publishTask.Wait();
            Thread.Sleep(300);

            var laterCount = 0;
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => Interlocked.Increment(ref laterCount));
            MyHub.EventHub.Publish("/thing1/StatusP", "changed", 999);
            Thread.Sleep(300);

            laterCount.Should().Be(1);
        }

        [TestMethod]
        public void RemoteEventLoopShouldStayAliveWhenReceivingAndUnsubscribingConcurrently()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => { });

            var publishTask = Task.Run(() =>
            {
                for (int i = 0; i < 300; i++)
                {
                    eventComm.EmitRemoteEvent(new EventArg(
                        "/thing1/StatusP",
                        "changed",
                        null,
                        "eventdummy://remote-host:1234"));
                }
            });

            Thread.Sleep(10);
            Action dispose = () => token.Dispose();

            dispose.Should().NotThrow();
            publishTask.Wait();
            Thread.Sleep(300);

            var laterCount = 0;
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref laterCount));
            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            Thread.Sleep(300);

            laterCount.Should().Be(1);
        }

        [TestMethod]
        public void RemoteEventWithoutHostShouldBeDropped()
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
        public void NullLocalEventShouldNotStopLocalEventLoop()
        {
            var handledCount = 0;

            MyHub.EventHub.Publish(null);
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => Interlocked.Increment(ref handledCount));
            MyHub.EventHub.Publish("/thing1/StatusP", "changed", 1);
            Thread.Sleep(300);

            handledCount.Should().Be(1);
        }

        [TestMethod]
        public void LocalFilterExceptionShouldNotStopLocalEventLoop()
        {
            var handledCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter(".", "changed"),
                e => { });
            MyHub.EventHub.Publish(new EventArg(null, "changed", null));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed"),
                e => Interlocked.Increment(ref handledCount));
            MyHub.EventHub.Publish("/thing1/StatusP", "changed", 1);
            Thread.Sleep(500);

            handledCount.Should().Be(1);
        }

        [TestMethod]
        public void RemoteFilterExceptionShouldNotStopRemoteEventLoop()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var handledCount = 0;

            MyHub.EventHub.Subscribe(
                new EventFilter(".", "changed", 0, "eventdummy://remote-host:1234"),
                e => { });
            eventComm.EmitRemoteEvent(new EventArg(
                null,
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref handledCount));
            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            Thread.Sleep(500);

            handledCount.Should().Be(1);
        }
    }
}

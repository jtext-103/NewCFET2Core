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
    public class EventHubStage9Test : CFET2Host
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
        public void RemoteTokenDisposeShouldBeIdempotent()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => { });

            token.Dispose();
            Action secondDispose = () => token.Dispose();

            secondDispose.Should().NotThrow();
            eventComm.UnsubscribeCount.Should().Be(1);
            eventComm.SubscribedTokenCount.Should().Be(0);
        }

        [TestMethod]
        public void FailedRemoteUnsubscribeShouldStillClearLocalAndModuleMappings()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var remoteCount = 0;

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref remoteCount));

            eventComm.ThrowOnUnsubscribe = true;
            Action dispose = () => token.Dispose();

            dispose.Should().NotThrow();
            eventComm.UnsubscribeCount.Should().Be(1);
            eventComm.SubscribedTokenCount.Should().Be(0);

            eventComm.ThrowOnUnsubscribe = false;
            eventComm.EmitRemoteEvent(new EventArg(
                "/thing1/StatusP",
                "changed",
                null,
                "eventdummy://remote-host:1234"));
            Thread.Sleep(300);

            remoteCount.Should().Be(0);
        }

        [TestMethod]
        public void RemoteEventLoopShouldNotThrowWhenUnsubscribingDuringHighFrequencyCallbacks()
        {
            var eventComm = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(eventComm);
            MyHub.StartCommunication();
            var handledCount = 0;

            var token = MyHub.EventHub.Subscribe(
                new EventFilter("/thing1/StatusP", "changed", 0, "eventdummy://remote-host:1234"),
                e => Interlocked.Increment(ref handledCount));

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

            eventComm.UnsubscribeCount.Should().Be(1);
        }
    }
}

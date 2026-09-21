#nullable disable
#pragma warning disable CS0612
using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class EventHubDisposalTest
    {
        [TestMethod]
        public void IdleLoopsShouldFinishAndReleaseHandlesOnDispose()
        {
            // Repeat on independent hubs: assert tasks, not noisy process-wide thread counts.
            for (int i = 0; i < 10; i++)
            {
                using var hub = new EventHub();
                hub.Init();
                var local = Field<Task>(hub, "localLoopTask");
                var remote = Field<Task>(hub, "remoteLoopTask");
                var localSignal = Field<AutoResetEvent>(hub, "eventPendingLocalEvent");
                var remoteSignal = Field<AutoResetEvent>(hub, "eventPendingRemoteEvent");
                hub.Dispose();
                Assert.AreEqual(TaskStatus.RanToCompletion, local.Status);
                Assert.AreEqual(TaskStatus.RanToCompletion, remote.Status);
                Assert.ThrowsExactly<ObjectDisposedException>(() => localSignal.WaitOne(0));
                Assert.ThrowsExactly<ObjectDisposedException>(() => remoteSignal.WaitOne(0));
            }
        }

        [TestMethod]
        public void InitShouldBeIdempotentAndRejectRestartAfterDispose()
        {
            using var hub = new EventHub();
            hub.Init();
            var local = Field<Task>(hub, "localLoopTask");
            var remote = Field<Task>(hub, "remoteLoopTask");
            hub.Init();
            Assert.AreSame(local, Field<Task>(hub, "localLoopTask"));
            Assert.AreSame(remote, Field<Task>(hub, "remoteLoopTask"));
            hub.Dispose();
            hub.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => hub.Init());
            Assert.ThrowsExactly<ObjectDisposedException>(() => hub.Subscribe(new EventFilter("/test", "changed"), _ => { }));
        }

        [TestMethod]
        public void DisposeBeforeInitShouldClearQueuedEventsAndRemainSafe()
        {
            using var hub = new EventHub();
            hub.Publish("/test", "changed", 1);
            using var token = hub.Subscribe(new EventFilter("/test", "changed"), _ => Assert.Fail());
            hub.Dispose();
            hub.Dispose();
            hub.Publish("/test", "changed", 2);
            Receive(hub)(RemoteEvent());
            Assert.AreEqual(0, QueueCount(hub, "pendingLocalEvent"));
            Assert.AreEqual(0, QueueCount(hub, "pendingRemoteEvent"));
        }

        [TestMethod]
        public void PublishingAndSubscribingDuringConcurrentDisposeShouldNotAccessReleasedHandles()
        {
            using var hub = new EventHub();
            hub.Init();
            var receive = Receive(hub);
            using var started = new CountdownEvent(3);
            using var stop = new CancellationTokenSource();
            var local = Task.Run(() =>
            {
                started.Signal();
                while (!stop.IsCancellationRequested) hub.Publish("/test", "changed", 1);
            });
            var remote = Task.Run(() =>
            {
                started.Signal();
                while (!stop.IsCancellationRequested) receive(RemoteEvent());
            });
            var subscriptions = Task.Run(() =>
            {
                started.Signal();
                while (!stop.IsCancellationRequested)
                {
                    try { using var token = hub.Subscribe(new EventFilter("/test", "changed"), _ => { }); }
                    catch (ObjectDisposedException) { return; }
                }
            });
            try
            {
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(3)));
                Assert.IsTrue(Task.WaitAll(new[] { Task.Run(hub.Dispose), Task.Run(hub.Dispose) }, TimeSpan.FromSeconds(3)));
            }
            finally
            {
                stop.Cancel();
                Assert.IsTrue(Task.WaitAll(new[] { local, remote, subscriptions }, TimeSpan.FromSeconds(3)));
            }
            Assert.AreEqual(TaskStatus.RanToCompletion, Field<Task>(hub, "localLoopTask").Status);
            Assert.AreEqual(TaskStatus.RanToCompletion, Field<Task>(hub, "remoteLoopTask").Status);
            Assert.AreEqual(0, QueueCount(hub, "pendingLocalEvent"));
            Assert.AreEqual(0, QueueCount(hub, "pendingRemoteEvent"));
        }

        [TestMethod]
        public void PendingLocalAndRemoteQueuesShouldBeDiscardedOnDispose()
        {
            using var hub = new EventHub();
            var receive = Receive(hub);
            for (int i = 0; i < 1000; i++)
            {
                hub.Publish("/test", "changed", i);
                receive(RemoteEvent());
            }
            hub.Init();
            hub.Dispose();
            Assert.AreEqual(TaskStatus.RanToCompletion, Field<Task>(hub, "localLoopTask").Status);
            Assert.AreEqual(TaskStatus.RanToCompletion, Field<Task>(hub, "remoteLoopTask").Status);
            Assert.AreEqual(0, QueueCount(hub, "pendingLocalEvent"));
            Assert.AreEqual(0, QueueCount(hub, "pendingRemoteEvent"));
        }

        [TestMethod]
        public void DisposeShouldNotWaitForAlreadyRunningUserHandler()
        {
            using var hub = new EventHub();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            hub.Init();
            using var token = hub.Subscribe(new EventFilter("/test", "changed"), _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                finished.Set();
            });
            hub.Publish("/test", "changed", 1);
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
                Assert.IsTrue(Task.Run(hub.Dispose).Wait(TimeSpan.FromSeconds(2)));
                Assert.IsFalse(finished.IsSet);
                Assert.IsTrue(Field<Task>(hub, "localLoopTask").IsCompleted);
            }
            finally
            {
                release.Set();
                Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(3)));
            }
        }

        [TestMethod]
        public void HandlerShouldBeAbleToDisposeItsOwnHub()
        {
            using var hub = new EventHub();
            hub.Init();
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var token = hub.Subscribe(new EventFilter("/test", "changed"), _ =>
            {
                hub.Dispose();
                finished.TrySetResult(true);
            });
            hub.Publish("/test", "changed", 1);
            Assert.IsTrue(finished.Task.Wait(TimeSpan.FromSeconds(3)));
            Assert.IsTrue(Field<Task>(hub, "localLoopTask").IsCompleted);
            Assert.IsTrue(Field<Task>(hub, "remoteLoopTask").IsCompleted);
        }

        [TestMethod]
        public void DisposeShouldCancelObservationWithoutWaitingForBlockedGetter()
        {
            var interval = GlobalConfig.ResourceObservationIntervalMs;
            GlobalConfig.ResourceObservationIntervalMs = 20;
            var host = new CFET2Host();
            HubMaster.InjectHubToModule(host);
            var thing = new BlockingGetterThing();
            try
            {
                host.MyHub.TryAddThing(thing, "/", "thing");
                using var subscription = host.MyHub.SubscribeResource("/thing/Value", _ => Assert.Fail("No event expected after shutdown."));
                Assert.IsTrue(thing.Entered.Wait(TimeSpan.FromSeconds(3)));
                var hub = host.MyHub.EventHub;
                var manager = Field<object>(hub, "resourceObservationManager");
                var observations = Field<IDictionary>(manager, "observations");
                Task observeTask = null;
                foreach (var observation in observations.Values)
                    observeTask = (Task)observation.GetType().GetProperty("ObserveTask").GetValue(observation);
                Assert.IsTrue(Task.Run(hub.Dispose).Wait(TimeSpan.FromSeconds(2)));
                Assert.IsFalse(observeTask.IsCompleted);
                thing.Release.Set();
                Assert.IsTrue(observeTask.Wait(TimeSpan.FromSeconds(3)));
                Assert.AreEqual(TaskStatus.RanToCompletion, observeTask.Status);
            }
            finally
            {
                thing.Release.Set();
                Hub.KillMaster();
                GlobalConfig.ResourceObservationIntervalMs = interval;
            }
        }

        private static T Field<T>(object instance, string name) =>
            (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        private static int QueueCount(EventHub hub, string name) =>
            (int)Field<object>(hub, name).GetType().GetProperty("Count").GetValue(Field<object>(hub, name));
        private static Action<EventArg> Receive(EventHub hub) => (Action<EventArg>)typeof(EventHub)
            .GetMethod("ReceiveRemoteEvent", BindingFlags.Instance | BindingFlags.NonPublic)
            .CreateDelegate(typeof(Action<EventArg>), hub);
        private static EventArg RemoteEvent() => new EventArg("/test", "changed", new Status<int>(1), "test://localhost:9999");

        public class BlockingGetterThing : Thing
        {
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            private int reads;
            [Cfet2Status]
            public int Value
            {
                get
                {
                    if (Interlocked.Increment(ref reads) == 1) return 1;
                    Entered.Set();
                    Release.Wait(TimeSpan.FromSeconds(5));
                    return 2;
                }
            }
        }
    }
}

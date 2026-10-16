using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize, TestCategory("CoreEventTransport")]
    public class LocalEventObserverTests : EventTransportTestBase
    {
        [TestMethod]
        public void ObservesWithoutBusinessSubscribersAndPreservesEventMetadata()
        {
            using var done = new ManualResetEventSlim();
            EventArg? received = null;
            MyHub.RegisterLocalEventObserver(ev => { received = ev; done.Set(); });
            var timestamp = DateTimeOffset.Parse("2026-10-15T10:00:00.1234567+08:00");
            MyHub.StartThings();
            MyHub.Publish(new EventArg("/Device/Temperature/2", "Changed", new Status<int>(42) { Timestamp = timestamp }));
            Await(done);
            Assert.AreEqual("/Device/Temperature/2", received!.Source);
            Assert.AreEqual("Changed", received.EventType);
            Assert.AreEqual(42, received.Sample.GetVal<int>());
            Assert.AreEqual(timestamp.ToUniversalTime(), received.Sample.Timestamp);
            Assert.AreEqual(0, MyHub.ObservedResourceCount, "Event observation must not start resource polling.");
        }

        [TestMethod]
        public void ObserverSnapshotPrecedesBusinessMutation()
        {
            using var done = new ManualResetEventSlim();
            string? snapshot = null;
            MyHub.RegisterLocalEventObserver(ev => snapshot = JsonConvert.SerializeObject(ev.Sample.Context));
            using var business = MyHub.Subscribe(new EventFilter("/device/value", "changed"), ev =>
            {
                ((JObject)ev.Sample.ObjectVal)["value"] = 99;
                done.Set();
            });
            MyHub.StartThings();
            MyHub.Publish("/device/value", "changed", new JObject { ["value"] = 42 });
            Await(done);
            Assert.AreEqual(42, JObject.Parse(snapshot!)[SampleBase<object>.KeyOfVal]!["value"]!.Value<int>());
        }

        [TestMethod]
        public void RemoteIngressDoesNotReachLocalObserver()
        {
            using var done = new ManualResetEventSlim();
            var received = new ConcurrentQueue<string>();
            MyHub.RegisterLocalEventObserver(ev => { received.Enqueue(ev.Source); done.Set(); });
            MyHub.StartThings();
            MyHub.EventHub.ReceiveRemoteEvent(new EventArg("/remote", "changed", null, "test://device:9000"));
            MyHub.Publish(new EventArg("/local", "alarm", null));
            Await(done);
            CollectionAssert.AreEqual(new[] { "/local" }, received.ToArray());
        }

        [TestMethod]
        public void ObserverExceptionIsLoggedWithoutPayloadAndDoesNotStopOtherDispatch()
        {
            var log = new RecordingLogger();
            SetField(MyHub.EventHub, "logger", log);
            using var done = new CountdownEvent(2);
            MyHub.RegisterLocalEventObserver(_ => throw new InvalidOperationException("secret payload"));
            MyHub.RegisterLocalEventObserver(_ => done.Signal());
            using var business = MyHub.Subscribe(new EventFilter("/test", "alarm"), _ => done.Signal());
            MyHub.StartThings();
            MyHub.Publish("/test", "alarm", "secret payload");
            Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(4)));
            Assert.AreEqual(1, log.Errors.Count);
            StringAssert.Contains(log.Errors.Single(), nameof(InvalidOperationException));
            Assert.IsFalse(log.Errors.Single().Contains("secret payload"));
        }

        [TestMethod]
        public void DuplicateStartupRegistrationsRemainUntilCoreExit()
        {
            using var done = new ManualResetEventSlim();
            var calls = 0;
            Action<EventArg> callback = _ => Interlocked.Increment(ref calls);
            MyHub.RegisterLocalEventObserver(callback);
            MyHub.RegisterLocalEventObserver(callback);
            MyHub.RegisterLocalEventObserver(_ => done.Set());
            MyHub.StartThings();
            MyHub.Publish("/test", "alarm", 1);
            Await(done);
            Assert.AreEqual(2, calls);
            done.Reset();
            MyHub.Publish("/test", "alarm", 2);
            Await(done);
            Assert.AreEqual(4, calls);
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalEventObserver(callback));
        }

        [TestMethod]
        public void CoreExitDuringEarlierCallbackSuppressesLaterSnapshotEntries()
        {
            using var done = new ManualResetEventSlim();
            MyHub.RegisterLocalEventObserver(_ => { MyHub.DisposeThings(); done.Set(); });
            var calls = 0;
            MyHub.RegisterLocalEventObserver(_ => calls++);
            MyHub.StartThings();
            MyHub.Publish("/test", "alarm", 1);
            Await(done);
            Assert.AreEqual(0, calls);
            Assert.ThrowsExactly<ObjectDisposedException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
        }

        [TestMethod]
        public void CallbackRunsOutsideStateLocksAndRejectsNewRegistrations()
        {
            using var done = new ManualResetEventSlim();
            var completed = false;
            var rejected = false;
            MyHub.RegisterLocalEventObserver(_ =>
            {
                var registration = Task.Run(() =>
                {
                    try { MyHub.RegisterLocalEventObserver(_ => { }); }
                    catch (InvalidOperationException) { rejected = true; }
                });
                completed = registration.Wait(TimeSpan.FromSeconds(2));
                done.Set();
            });
            MyHub.StartThings();
            MyHub.Publish("/test", "alarm", 1);
            Await(done);
            Assert.IsTrue(completed, "Callback must not hold the registration lock.");
            Assert.IsTrue(rejected);
        }

        [TestMethod]
        public void ClosingCoreObserversDoesNotWaitForAlreadyEnteredCallback()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            var calls = 0;
            MyHub.RegisterLocalEventObserver(_ => { calls++; entered.Set(); release.Wait(TimeSpan.FromSeconds(4)); done.Set(); });
            var laterCalls = 0;
            MyHub.RegisterLocalEventObserver(_ => laterCalls++);
            MyHub.StartThings();
            MyHub.Publish("/test", "alarm", 1);
            try
            {
                Await(entered);
                Assert.IsTrue(Task.Run(MyHub.DisposeThings).Wait(TimeSpan.FromSeconds(1)));
            }
            finally { release.Set(); }
            Await(done);
            // The direct final boundary is synchronous, so no sleep is needed to prove suppression.
            Notify()(new EventArg("/test", "alarm", null));
            Assert.AreEqual(1, calls);
            Assert.AreEqual(0, laterCalls);
        }

        [TestMethod]
        public void ObserverCanDisposeEventHubWithoutDeadlockOrLaterDelivery()
        {
            using var done = new ManualResetEventSlim();
            var eventHub = MyHub.EventHub;
            MyHub.RegisterLocalEventObserver(_ => { eventHub.Dispose(); done.Set(); });
            var calls = 0;
            MyHub.RegisterLocalEventObserver(_ => calls++);
            MyHub.StartThings();
            MyHub.Publish("/test", "alarm", 1);
            Await(done);
            Assert.AreEqual(0, calls);
            Assert.ThrowsExactly<ObjectDisposedException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
        }

        [TestMethod]
        public void RejectsNullCallbackAndRegistrationAfterShutdown()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => MyHub.RegisterLocalEventObserver(null!));
            var oldHub = MyHub;
            Hub.KillMaster();
            HubMaster.InjectHubToModule(this);
            Assert.ThrowsExactly<ObjectDisposedException>(() => oldHub.RegisterLocalEventObserver(_ => { }));
            MyHub.RegisterLocalEventObserver(_ => { });
        }

        [TestMethod]
        public void StartupEventIsDeliveredBeforeReadyWithoutWaitingForStartupCompletion()
        {
            using var done = new ManualResetEventSlim();
            var readyAtDelivery = true;
            MyHub.RegisterLocalEventObserver(_ => { readyAtDelivery = MyHub.IsLocalResourcesReady; done.Set(); });
            MyHub.TryAddThing(new LocalResourceReadinessTests.StartupDevice
            {
                OnStart = () => { MyHub.Publish("/startup", "alarm", 1); Await(done); }
            }, "/", "device");
            MyHub.StartThings();
            Assert.IsFalse(readyAtDelivery);
            Assert.IsTrue(MyHub.IsLocalResourcesReady);
        }

        [TestMethod]
        public void NotificationDoesNotAllocatePerEventOrAcquireLifecycleLock()
        {
            var calls = 0;
            MyHub.RegisterLocalEventObserver(_ => calls++);
            MyHub.StartThings();
            var notify = Notify();
            var message = new EventArg("/test", "alarm", null);
            notify(message); // Warm up before measuring the notification boundary only.
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++) notify(message);
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.AreEqual(10001, calls);

            var stateLock = Field<object>(MyHub.EventHub, "lifecycleLock");
            Task delivery;
            Monitor.Enter(stateLock);
            try
            {
                delivery = Task.Run(() => notify(message));
                Assert.IsTrue(delivery.Wait(TimeSpan.FromSeconds(2)), "Notification must not wait for the lifecycle lock.");
            }
            finally { Monitor.Exit(stateLock); }
            Assert.AreEqual(10002, calls);
        }

        [TestMethod]
        public void NormalBusinessSubscriptionsRemainDynamicAfterReady()
        {
            var observerCalls = 0;
            MyHub.RegisterLocalEventObserver(_ => observerCalls++);
            MyHub.StartThings();
            using var done = new ManualResetEventSlim();
            using var business = MyHub.Subscribe(new EventFilter("/test", "alarm"), _ => done.Set());
            MyHub.Publish("/test", "alarm", 1);
            Await(done);
            Assert.AreEqual(1, observerCalls);
        }

        [TestMethod]
        public void RegistrationCanRacePublicationWhoseSampleQueriesReadiness()
        {
            using var evaluating = new ManualResetEventSlim();
            using var readReady = new ManualResetEventSlim();
            using var registering = new ManualResetEventSlim();
            var sample = new ReadinessSample(() =>
            {
                evaluating.Set();
                readReady.Wait(TimeSpan.FromSeconds(4));
                return MyHub.IsLocalResourcesReady ? 1 : 0;
            });
            var publication = Task.Run(() => MyHub.Publish("/test", "alarm", sample));
            Await(evaluating);
            var registration = Task.Run(() =>
            {
                registering.Set();
                MyHub.RegisterLocalEventObserver(_ => { });
            });
            try
            {
                Await(registering);
            }
            finally { readReady.Set(); }
            Assert.IsTrue(Task.WaitAll(new[] { publication, registration }, TimeSpan.FromSeconds(4)),
                "Sample readiness reads and observer registration must not acquire two locks in opposite order.");
        }

        [TestMethod]
        public void AssemblyEventsReachBusinessOnlyAndAreNotReplayed()
        {
            using var businessDone = new ManualResetEventSlim();
            using var observerDone = new ManualResetEventSlim();
            var events = new ConcurrentQueue<string>();
            MyHub.RegisterLocalEventObserver(ev => { events.Enqueue(ev.Source); observerDone.Set(); });
            using var subscription = MyHub.Subscribe(new EventFilter("/assembly", "alarm"), _ => businessDone.Set());
            MyHub.Publish("/assembly", "alarm", 1);
            Await(businessDone); // Outlet processing precedes the business callback.
            Assert.AreEqual(0, events.Count);
            MyHub.StartThings();
            MyHub.Publish("/started", "alarm", 2);
            Await(observerDone);
            CollectionAssert.AreEqual(new[] { "/started" }, events.ToArray());
        }

        [TestMethod]
        public void QueuedAssemblyEventIsExcludedEvenWhenConsumedAfterOutletStarts()
        {
            using var eventHub = new EventHub(); // Deliberately do not start its consumer yet.
            using var done = new ManualResetEventSlim();
            var events = new ConcurrentQueue<string>();
            eventHub.RegisterLocalEventObserver(ev => { events.Enqueue(ev.Source); done.Set(); });
            Assert.IsTrue(eventHub.TryPublish(new EventArg("/assembly", "alarm", null)));
            lock (eventHub.ObserverRegistrationLock) eventHub.StartLocalEventObservers();
            Assert.IsTrue(eventHub.TryPublish(new EventArg("/started", "alarm", null)));
            eventHub.Init();
            Await(done);
            CollectionAssert.AreEqual(new[] { "/started" }, events.ToArray());
        }

        private sealed class ReadinessSample(Func<int> read) : SampleBase<int>
        {
            public override int Val => read();
        }

        private Action<EventArg> Notify() => (Action<EventArg>)typeof(EventHub)
            .GetMethod("NotifyLocalEventObservers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<EventArg>), MyHub.EventHub);
    }
}

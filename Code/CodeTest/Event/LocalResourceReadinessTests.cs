using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize, TestCategory("CoreEventTransport")]
    public class LocalResourceReadinessTests : EventTransportTestBase
    {
        [TestMethod]
        public void InvokesSynchronouslyAfterThingsAndAutomaticObservationInitialization()
        {
            // Configuration is captured before creating a replacement Core.
            Hub.KillMaster();
            GlobalConfig.AutoObserveList = new[] { new ObservationListRule(path: "/device/Value") };
            HubMaster.InjectHubToModule(this);
            var device = new StartupDevice();
            MyHub.TryAddThing(device, "/", "device");
            var thread = Environment.CurrentManagedThreadId;
            var callbackThread = 0;
            var readyDuringCallback = false;
            var initializationComplete = false;
            MyHub.RegisterLocalResourcesReadyObserver(() =>
            {
                callbackThread = Environment.CurrentManagedThreadId;
                readyDuringCallback = MyHub.IsLocalResourcesReady;
                initializationComplete = device.Started && device.Reads == 1 && MyHub.AutoObservationResults.Count == 1;
            });
            Assert.IsFalse(MyHub.IsLocalResourcesReady);
            MyHub.StartCommunication();
            Assert.IsFalse(MyHub.IsLocalResourcesReady);
            MyHub.StartThings();
            Assert.AreEqual(thread, callbackThread, "Ready is a direct Invoke on the StartThings caller.");
            Assert.IsTrue(readyDuringCallback);
            Assert.IsTrue(initializationComplete);
            Assert.IsTrue(MyHub.IsLocalResourcesReady);
            HubMaster.getInstance().MarkLocalResourcesReady();
            Assert.AreEqual(thread, callbackThread);
        }

        [TestMethod]
        public void StartupRegistrationReceivesExactlyOneNotification()
        {
            var calls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(() => calls++);
            MyHub.StartThings();
            HubMaster.getInstance().MarkLocalResourcesReady();
            Assert.AreEqual(1, calls);
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
        }

        [TestMethod]
        public void CoreOwnsAllStartupRegistrationsAndNotifiesEachInOrder()
        {
            var calls = new System.Collections.Generic.List<int>();
            MyHub.RegisterLocalResourcesReadyObserver(() => calls.Add(1));
            MyHub.RegisterLocalResourcesReadyObserver(() => calls.Add(2));
            MyHub.StartThings();
            HubMaster.getInstance().MarkLocalResourcesReady();
            CollectionAssert.AreEqual(new[] { 1, 2 }, calls);
        }

        [TestMethod]
        public void ReadyCallbackRunsOutsideLocksAndRejectsReentrantRegistrations()
        {
            var completed = false;
            var rejected = false;
            var calls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(() =>
            {
                var task = Task.Run(() =>
                {
                    try { MyHub.RegisterLocalResourcesReadyObserver(() => { }); }
                    catch (InvalidOperationException) { rejected = true; }
                });
                completed = task.Wait(TimeSpan.FromSeconds(2));
            });
            MyHub.RegisterLocalResourcesReadyObserver(() => calls++);
            MyHub.StartThings();
            Assert.IsTrue(completed, "Ready callbacks must run outside startup locks.");
            Assert.IsTrue(rejected);
            Assert.AreEqual(1, calls);
            Assert.IsTrue(MyHub.IsLocalResourcesReady);
        }

        [TestMethod]
        public void ReadyCallbackExceptionsAreIsolatedAndLogged()
        {
            var log = new RecordingLogger();
            SetField(HubMaster.getInstance(), "readinessLogger", log);
            var calls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(() => throw new InvalidOperationException("secret sample"));
            MyHub.RegisterLocalResourcesReadyObserver(() => calls++);
            MyHub.StartThings();
            Assert.IsTrue(MyHub.IsLocalResourcesReady);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, log.Errors.Count);
            StringAssert.Contains(log.Errors.Single(), nameof(InvalidOperationException));
            Assert.IsFalse(log.Errors.Single().Contains("secret sample"));
        }

        [TestMethod]
        public void ThingStartupFailurePropagatesOriginalErrorWithoutReady()
        {
            var failure = new InvalidOperationException("device startup failed");
            MyHub.TryAddThing(new StartupDevice { OnStart = () => throw failure }, "/", "device");
            var calls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(() => calls++);
            Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(MyHub.StartThings));
            Assert.IsFalse(MyHub.IsLocalResourcesReady);
            Assert.AreEqual(0, calls);
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
            HubMaster.getInstance().MarkLocalResourcesReady();
            Assert.IsFalse(MyHub.IsLocalResourcesReady, "Failed startup must not reopen or announce ready later.");
        }

        [TestMethod]
        public void AutoObservationInitializationFailureDoesNotAnnounceReady()
        {
            var calls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(() => calls++);
            MyHub.EventHub.StopResourceObservations();
            Assert.ThrowsExactly<ObjectDisposedException>(MyHub.StartThings);
            Assert.IsFalse(MyHub.IsLocalResourcesReady);
            Assert.AreEqual(0, calls);
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
        }

        [TestMethod]
        public void IndividualObservationFailureIsDiagnosticRatherThanStartupFailure()
        {
            Hub.KillMaster();
            GlobalConfig.AutoObserveList = new[] { new ObservationListRule(path: "/device/Value") };
            HubMaster.InjectHubToModule(this);
            MyHub.TryAddThing(new StartupDevice { FailRead = true }, "/", "device");
            var calls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(() => calls++);
            MyHub.StartThings();
            Assert.IsTrue(MyHub.IsLocalResourcesReady);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(ResourceObservationStatus.Failed, MyHub.AutoObservationResults.Single().Status);
        }

        [TestMethod]
        public void RegistrationDuringNotificationIsAlreadyTooLate()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            MyHub.RegisterLocalResourcesReadyObserver(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(4)); });
            var startup = Task.Run(MyHub.StartThings);
            try
            {
                Await(entered);
                Assert.IsTrue(MyHub.IsLocalResourcesReady);
                Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
                Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
                Assert.IsFalse(startup.IsCompleted, "Callback is directly invoked, not an untracked background task.");
            }
            finally { release.Set(); }
            Assert.IsTrue(startup.Wait(TimeSpan.FromSeconds(4)));
        }

        [TestMethod]
        public void ClosingDuringNotificationSuppressesLaterCallbacks()
        {
            var calls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(MyHub.DisposeThings);
            MyHub.RegisterLocalResourcesReadyObserver(() => calls++);
            MyHub.StartThings();
            Assert.IsFalse(MyHub.IsLocalResourcesReady);
            Assert.AreEqual(0, calls);
            Assert.ThrowsExactly<ObjectDisposedException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
            Assert.ThrowsExactly<ObjectDisposedException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
        }

        [TestMethod]
        public void LateStartupCompletionCannotAffectReplacementCore()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            MyHub.TryAddThing(new StartupDevice { OnStart = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(4)); } }, "/", "device");
            var oldHub = MyHub;
            var calls = 0;
            oldHub.RegisterLocalResourcesReadyObserver(() => calls++);
            var startup = Task.Run(() => { try { oldHub.StartThings(); } catch (ObjectDisposedException) { } });
            try
            {
                Await(entered);
                Hub.KillMaster();
                HubMaster.InjectHubToModule(this);
            }
            finally { release.Set(); }
            Assert.IsTrue(startup.Wait(TimeSpan.FromSeconds(4)));
            Assert.IsFalse(oldHub.IsLocalResourcesReady);
            Assert.IsFalse(MyHub.IsLocalResourcesReady);
            Assert.AreEqual(0, calls);
            Assert.ThrowsExactly<ObjectDisposedException>(() => oldHub.RegisterLocalResourcesReadyObserver(() => { }));
            Assert.ThrowsExactly<ObjectDisposedException>(oldHub.StartThings);
            MyHub.StartThings();
            Assert.IsTrue(MyHub.IsLocalResourcesReady);
            Assert.IsFalse(oldHub.IsLocalResourcesReady);
        }

        [TestMethod]
        public void ClosingBeforeStartupRejectsRegistrationsAndDoesNotNotify()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => MyHub.RegisterLocalResourcesReadyObserver(null!));
            var calls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(() => calls++);
            MyHub.DisposeThings();
            HubMaster.getInstance().MarkLocalResourcesReady();
            Assert.IsFalse(MyHub.IsLocalResourcesReady);
            Assert.AreEqual(0, calls);
            Assert.ThrowsExactly<ObjectDisposedException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
        }

        [TestMethod]
        public void ObserversAreFixedBeforeThingStartupBegins()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var eventDelivered = new ManualResetEventSlim();
            MyHub.TryAddThing(new StartupDevice
            {
                OnStart = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(4)); }
            }, "/", "device");
            var readyCalls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(() => readyCalls++);
            MyHub.RegisterLocalEventObserver(_ => eventDelivered.Set());
            var startup = Task.Run(MyHub.StartThings);
            try
            {
                Await(entered);
                Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
                Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
                MyHub.Publish("/startup", "alarm", 1);
                Await(eventDelivered);
                Assert.IsFalse(MyHub.IsLocalResourcesReady);
            }
            finally { release.Set(); }
            Assert.IsTrue(startup.Wait(TimeSpan.FromSeconds(4)));
            Assert.AreEqual(1, readyCalls);
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
        }

        [TestMethod]
        [DataRow(false)] [DataRow(true)]
        public void RegistrationRacingStartupIsEitherAcceptedOrRejectedWithoutLosingCallbacks(bool readyObservers)
        {
            using var start = new ManualResetEventSlim();
            var accepted = new int[64];
            var received = new int[64];
            var startup = Task.Run(() => { start.Wait(); MyHub.StartThings(); });
            var registrations = Enumerable.Range(0, accepted.Length).Select(index => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    if (readyObservers) MyHub.RegisterLocalResourcesReadyObserver(() => Interlocked.Increment(ref received[index]));
                    else MyHub.RegisterLocalEventObserver(_ => Interlocked.Increment(ref received[index]));
                    accepted[index] = 1;
                }
                catch (InvalidOperationException) { }
            })).ToArray();
            start.Set();
            Assert.IsTrue(Task.WaitAll(registrations.Append(startup).ToArray(), TimeSpan.FromSeconds(6)));
            if (!readyObservers)
            {
                var notify = (Action<EventArg>)typeof(EventHub)
                    .GetMethod("NotifyLocalEventObservers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .CreateDelegate(typeof(Action<EventArg>), MyHub.EventHub);
                notify(new EventArg("/test", "alarm", null));
            }
            CollectionAssert.AreEqual(accepted, received);
            Assert.IsTrue(MyHub.IsLocalResourcesReady);
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
        }

        [TestMethod]
        public void FirstThingCannotRegisterOrRestartCoreButCanPublishBeforeReady()
        {
            using var delivered = new ManualResetEventSlim();
            MyHub.RegisterLocalEventObserver(_ => delivered.Set());
            MyHub.TryAddThing(new StartupDevice { OnStart = () =>
            {
                Assert.IsFalse(MyHub.IsLocalResourcesReady);
                Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalEventObserver(_ => { }));
                Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.RegisterLocalResourcesReadyObserver(() => { }));
                Assert.ThrowsExactly<InvalidOperationException>(MyHub.StartThings);
                MyHub.Publish("/startup", "alarm", 1);
                Await(delivered);
            } }, "/", "first");
            MyHub.StartThings();
            Assert.IsTrue(MyHub.IsLocalResourcesReady);
            Assert.ThrowsExactly<InvalidOperationException>(MyHub.StartThings);
            Assert.IsTrue(MyHub.IsLocalResourcesReady, "Rejected repeat startup must not fault the running Core.");
        }

        [TestMethod]
        public void ReadyCannotBeAnnouncedBeforeStartupBegins()
        {
            var calls = 0;
            MyHub.RegisterLocalResourcesReadyObserver(() => calls++);
            HubMaster.getInstance().MarkLocalResourcesReady();
            Assert.IsFalse(MyHub.IsLocalResourcesReady);
            Assert.AreEqual(0, calls);
            MyHub.StartThings();
            Assert.IsTrue(MyHub.IsLocalResourcesReady);
            Assert.AreEqual(1, calls);
        }

        public sealed class StartupDevice : Thing
        {
            public bool Started;
            public int Reads;
            public bool FailRead;
            public Action? OnStart;
            public override void Start() { OnStart?.Invoke(); Started = true; }
            [Cfet2Status]
            public int Value
            {
                get
                {
                    Reads++;
                    if (!Started || FailRead) throw new InvalidOperationException("Device is unavailable.");
                    return 42;
                }
            }
        }
    }
}

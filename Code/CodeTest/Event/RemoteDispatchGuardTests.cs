using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Communication;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Test.TestDummies;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize, TestCategory("CoreEventTransport")]
    public class RemoteDispatchGuardTests : EventTransportTestBase
    {
        private const string Host = "eventdummy://device:9000";

        private GuardedModule RegisterGuard()
        {
            var module = new GuardedModule();
            MyHub.TryAddCommunicationModule(module);
            MyHub.StartCommunication();
            return module;
        }

        // Invoke the final dispatch boundary deterministically, including after cancellation.
        // This allows testing suppression without sleeps or assuming thread-pool scheduling order.
        private Action<EventArg> Dispatch(Token token)
        {
            var eventHub = MyHub.EventHub;
            var subscriptions = (Dictionary<Guid, Subscriber>)typeof(EventHub)
                .GetProperty("remoteSubDict", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(eventHub)!;
            var subscriber = subscriptions[token.Id];
            var invoke = (Action<Guid, Subscriber, EventArg>)typeof(EventHub)
                .GetMethod("invokeRemoteHandlerIfSubscribed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(Action<Guid, Subscriber, EventArg>), eventHub);
            return ev => invoke(token.Id, subscriber, ev);
        }

        private static EventArg Remote(string path = "/device/temp") => new EventArg(path, "changed", null, Host);

        [TestMethod]
        public void SuccessfulRuleContinuesWhileFailedRuleWithinSameTokenIsSuppressed()
        {
            var module = RegisterGuard();
            var filter = new EventFilter("/device/temp", "changed", 0, Host);
            filter.AddSourceAndType("/device/pressure", "changed");
            var received = new List<string>();
            using var token = MyHub.Subscribe(filter, ev => received.Add(ev.Source));
            module.Decide = (id, ev) => id == token.Id && ev.Source == "/device/temp";
            var dispatch = Dispatch(token);
            dispatch(Remote("/device/temp"));
            dispatch(Remote("/device/pressure"));
            dispatch(Remote("/device/temp"));
            CollectionAssert.AreEqual(new[] { "/device/temp", "/device/temp" }, received);
            Assert.AreEqual(3, module.Checks);
        }

        [TestMethod]
        public void EventArrivingThroughWideRuleCannotBypassAnotherTokensFailedRule()
        {
            var module = RegisterGuard();
            var failedCalls = 0;
            var healthyCalls = 0;
            using var failed = MyHub.Subscribe(new EventFilter("/device/temp", "changed", 0, Host), _ => failedCalls++);
            using var healthy = MyHub.Subscribe(new EventFilter("/device/.*", "changed", 0, Host), _ => healthyCalls++);
            module.Decide = (id, _) => id == healthy.Id;
            Dispatch(failed)(Remote());
            Dispatch(healthy)(Remote());
            Assert.AreEqual(0, failedCalls);
            Assert.AreEqual(1, healthyCalls);
        }

        [TestMethod]
        public void FinalCheckUsesCurrentRuleStateRatherThanRegistrationState()
        {
            var module = RegisterGuard();
            var calls = 0;
            using var token = MyHub.Subscribe(new EventFilter("/device/.*", "changed", 0, Host), _ => calls++);
            var dispatch = Dispatch(token);
            module.Decide = (_, _) => false;
            dispatch(Remote());
            Assert.AreEqual(0, calls);
            module.Decide = (_, _) => true;
            dispatch(Remote());
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public void ModuleWithoutGuardKeepsOriginalRemoteDelivery()
        {
            var module = new DummyEventCommunicationModule();
            MyHub.TryAddCommunicationModule(module);
            MyHub.StartCommunication();
            using var done = new ManualResetEventSlim();
            using var token = MyHub.Subscribe(new EventFilter("/device/temp", "changed", 0, Host), _ => done.Set());
            module.EmitRemoteEvent(Remote());
            Await(done);
        }

        [TestMethod]
        public void MatchingMultipleOriginalRulesStillProducesOneBusinessCallback()
        {
            var module = RegisterGuard();
            var filter = new EventFilter("/device/temp", "changed", 0, Host);
            filter.AddSourceAndType("/device/.*", "changed");
            using var done = new ManualResetEventSlim();
            var calls = 0;
            using var token = MyHub.Subscribe(filter, _ => { Interlocked.Increment(ref calls); done.Set(); });
            module.EmitRemoteEvent(Remote());
            Await(done);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, module.Checks);
        }

        [TestMethod]
        public void GuardDoesNotRunForLocalBusinessSubscriptions()
        {
            var module = RegisterGuard();
            module.Decide = (_, _) => throw new InvalidOperationException();
            using var done = new ManualResetEventSlim();
            using var token = MyHub.Subscribe(new EventFilter("/local", "changed"), _ => done.Set());
            MyHub.Publish("/local", "changed", 1);
            Await(done);
            Assert.AreEqual(0, module.Checks);
        }

        [TestMethod]
        public void GuardExceptionsLogTokenAndSuppressOnlyCurrentDelivery()
        {
            var module = RegisterGuard();
            var log = new RecordingLogger();
            SetField(MyHub.EventHub, "logger", log);
            var calls = 0;
            using var token = MyHub.Subscribe(new EventFilter("/device/temp", "changed", 0, Host), _ => calls++);
            var dispatch = Dispatch(token);
            module.Decide = (_, _) => throw new InvalidOperationException("secret payload");
            dispatch(Remote());
            Assert.AreEqual(0, calls);
            StringAssert.Contains(log.Errors.Single(), token.Id.ToString());
            StringAssert.Contains(log.Errors.Single(), Host);
            Assert.IsFalse(log.Errors.Single().Contains("secret payload"));
            module.Decide = (_, _) => true;
            dispatch(Remote());
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public void CancellationBeforeFinalDispatchSkipsGuardAndBusinessHandler()
        {
            var module = RegisterGuard();
            var calls = 0;
            var token = MyHub.Subscribe(new EventFilter("/device/temp", "changed", 0, Host), _ => calls++);
            var dispatch = Dispatch(token);
            token.Dispose();
            dispatch(Remote());
            Assert.AreEqual(0, module.Checks);
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        [DataRow(false)] [DataRow(true)]
        public void CancellationOrShutdownDuringGuardPreventsLateBusinessCallback(bool shutdown)
        {
            var module = RegisterGuard();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var calls = 0;
            using var token = MyHub.Subscribe(new EventFilter("/device/temp", "changed", 0, Host), _ => calls++);
            var dispatch = Dispatch(token);
            module.Decide = (_, _) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(4)); return true; };
            var pending = Task.Run(() => dispatch(Remote()));
            try
            {
                Await(entered);
                var close = Task.Run(() => { if (shutdown) MyHub.EventHub.Dispose(); else token.Dispose(); });
                Assert.IsTrue(close.Wait(TimeSpan.FromSeconds(2)), "Guard must run outside Core subscription/lifecycle locks.");
            }
            finally { release.Set(); }
            Assert.IsTrue(pending.Wait(TimeSpan.FromSeconds(4)));
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void OldDispatchCannotConsultOrDeliverIntoReplacementCore()
        {
            var module = RegisterGuard();
            var calls = 0;
            var token = MyHub.Subscribe(new EventFilter("/device/temp", "changed", 0, Host), _ => calls++);
            var dispatch = Dispatch(token);
            Hub.KillMaster();
            HubMaster.InjectHubToModule(this);
            dispatch(Remote());
            Assert.AreEqual(0, module.Checks);
            Assert.AreEqual(0, calls);
        }

        private sealed class GuardedModule : DummyEventCommunicationModule, IRemoteEventDispatchGuard
        {
            public Func<Guid, EventArg, bool> Decide = (_, _) => true;
            public int Checks;
            public bool ShouldDispatch(Guid tokenId, EventArg eventArg)
            {
                Interlocked.Increment(ref Checks);
                return Decide(tokenId, eventArg);
            }
        }
    }
}

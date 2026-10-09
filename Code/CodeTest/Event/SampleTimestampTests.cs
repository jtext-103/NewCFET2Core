using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    [DoNotParallelize]
    public class SampleTimestampTests : CFET2Host
    {
        private static readonly DateTimeOffset SourceTime = new DateTimeOffset(2026, 9, 24, 12, 30, 0, TimeSpan.FromHours(8)).AddTicks(1234567);

        [TestInitialize]
        public void Init() => HubMaster.InjectHubToModule(this);

        [TestCleanup]
        public void Cleanup() => Hub.KillMaster();

        [TestMethod]
        public void TimestampShouldNormalizeAndSurviveCopiesAndJson()
        {
            var sample = new Status<int>(12) { Timestamp = SourceTime };
            Assert.AreEqual(TimeSpan.Zero, sample.Timestamp.Value.Offset);
            Assert.AreEqual(SourceTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), sample.Context[SampleBase<int>.KeyOfTimestamp]);
            Assert.AreEqual(SourceTime, new SampleBase<long>(sample.Context).Timestamp);
            Assert.AreEqual(SourceTime, sample.ToStatus().Timestamp);
            Assert.AreEqual(SourceTime, sample.ToConfig().Timestamp);
            Assert.AreEqual(SourceTime, sample.ToMethod().Timestamp);
            var copy = JsonConvert.DeserializeObject<Status<int>>(JsonConvert.SerializeObject(sample));
            Assert.AreEqual(SourceTime, copy.Timestamp);
            sample.Timestamp = null;
            Assert.IsNull(sample.Timestamp);
            Assert.IsFalse(sample.Context.ContainsKey(SampleBase<int>.KeyOfTimestamp));
        }

        [TestMethod]
        public void MissingInvalidAndDateObjectTimestampsShouldFollowCompatibilityPolicy()
        {
            var sample = new Status<int>(1);
            Assert.IsNull(sample.Timestamp);
            foreach (var raw in new object[] { "bad-date", 123L, new object() })
            {
                sample.Context[SampleBase<int>.KeyOfTimestamp] = raw;
                Assert.IsNull(sample.Timestamp);
            }
            foreach (var raw in new object[] { SourceTime, SourceTime.UtcDateTime, SourceTime.ToString("O") })
            {
                sample.Context[SampleBase<int>.KeyOfTimestamp] = raw;
                Assert.AreEqual(SourceTime, sample.Timestamp);
                Assert.AreEqual(TimeSpan.Zero, sample.Timestamp.Value.Offset);
            }
        }

        [TestMethod]
        public void PublishShouldSnapshotAndStampEachReuseWithoutMutatingCaller()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var token = MyHub.Subscribe(new EventFilter("/value", "changed"), events.Enqueue);
            var sample = new Status<int>(1);
            var arg = new EventArg("/value", "changed", sample);
            var before = DateTimeOffset.UtcNow;
            MyHub.Publish(arg);
            var firstEnd = DateTimeOffset.UtcNow;
            Assert.IsTrue(SpinWait.SpinUntil(() => events.Count == 1, 3000));
            Assert.IsNull(sample.Timestamp);
            sample.Context[SampleBase<int>.KeyOfVal] = 2;
            var secondStart = DateTimeOffset.UtcNow;
            MyHub.Publish(arg);
            var secondEnd = DateTimeOffset.UtcNow;
            arg.Source = "/mutated";
            sample.Context[SampleBase<int>.KeyOfVal] = 3;
            Assert.IsTrue(SpinWait.SpinUntil(() => events.Count == 2, 3000));
            var received = events.ToArray();
            Assert.AreEqual(1, received[0].Sample.GetVal<int>());
            Assert.AreEqual(2, received[1].Sample.GetVal<int>());
            Assert.AreEqual("/value", received[1].Source);
            Assert.IsTrue(received[0].Sample.Timestamp >= before && received[0].Sample.Timestamp <= firstEnd);
            Assert.IsTrue(received[1].Sample.Timestamp >= secondStart && received[1].Sample.Timestamp <= secondEnd);
            Assert.AreNotSame(arg, received[0]);
            Assert.AreNotSame(sample.Context, received[0].Sample.Context);
            Assert.IsNull(sample.Timestamp);
        }

        [TestMethod]
        public void PublishSnapshotShouldPreserveEvaluatedValueTypeWithoutChangingOriginalContext()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var token = MyHub.Subscribe(new EventFilter("/value", "changed"), events.Enqueue);
            var sample = new Status<int>(0);
            sample.Context[SampleBase<int>.KeyOfVal] = "42";
            Assert.IsInstanceOfType<int>(sample.ObjectVal);

            MyHub.Publish(new EventArg("/value", "changed", sample));

            Assert.IsTrue(SpinWait.SpinUntil(() => events.Count == 1, 3000));
            var received = events.ToArray()[0].Sample;
            Assert.IsInstanceOfType<int>(received.ObjectVal);
            Assert.AreEqual(42, (int)received.ObjectVal);
            Assert.AreEqual("42", sample.Context[SampleBase<int>.KeyOfVal]);
            Assert.IsNull(sample.Timestamp);
            Assert.IsNotNull(received.Timestamp);
        }

        [TestMethod]
        public void PublishShouldPreserveExplicitTimeAndShareItAcrossSubscribers()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var first = MyHub.Subscribe(new EventFilter("/value", "changed"), events.Enqueue);
            using var second = MyHub.Subscribe(new EventFilter("/value", "changed"), events.Enqueue);
            var sample = new Status<int>(1) { Timestamp = SourceTime };
            MyHub.PublishResource("/value", sample);
            Assert.IsTrue(SpinWait.SpinUntil(() => events.Count == 2, 3000));
            foreach (var received in events) Assert.AreEqual(SourceTime, received.Sample.Timestamp);
            Assert.AreEqual(SourceTime, sample.Timestamp);
        }

        [TestMethod]
        public void RawValuePublicationShouldGetSourceTime()
        {
            var events = new ConcurrentQueue<EventArg>();
            using var token = MyHub.Subscribe(new EventFilter("/value", "changed"), events.Enqueue);
            var before = DateTimeOffset.UtcNow;
            MyHub.PublishResource("/value", 42);
            var after = DateTimeOffset.UtcNow;
            Assert.IsTrue(SpinWait.SpinUntil(() => events.Count == 1, 3000));
            Assert.IsTrue(events.ToArray()[0].Sample.Timestamp >= before && events.ToArray()[0].Sample.Timestamp <= after);
        }

        [TestMethod]
        public void ObservationShouldUseDetectionTimeOrExplicitTimeAndIgnoreUnchangedValues()
        {
            var thing = new TimestampThing();
            MyHub.TryAddThing(thing, "/", "thing");
            var events = new ConcurrentQueue<EventArg>();
            var subscription = MyHub.SubscribeResource("/thing/Value", events.Enqueue);
            try
            {
                Assert.IsNull(subscription.InitialValue.Timestamp);
                var before = DateTimeOffset.UtcNow;
                var changedSample = new Status<int>(0);
                changedSample.Context[SampleBase<int>.KeyOfVal] = "2";
                thing.Current = changedSample;
                Assert.IsTrue(SpinWait.SpinUntil(() => events.Count == 1, 4000));
                Assert.IsInstanceOfType<int>(events.ToArray()[0].Sample.ObjectVal);
                Assert.AreEqual(2, (int)events.ToArray()[0].Sample.ObjectVal);
                Assert.AreEqual("2", changedSample.Context[SampleBase<int>.KeyOfVal]);
                var detected = events.ToArray()[0].Sample.Timestamp;
                Assert.IsTrue(detected >= before && detected <= DateTimeOffset.UtcNow);
                Assert.IsNull(thing.Current.Timestamp);
                thing.Current = new Status<int>(3) { Timestamp = SourceTime };
                Assert.IsTrue(SpinWait.SpinUntil(() => events.Count == 2, 4000));
                Assert.AreEqual(SourceTime, events.ToArray()[1].Sample.Timestamp);
                // Wait for further reads, rather than relying on a fixed sleep.
                var reads = Volatile.Read(ref thing.Reads);
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref thing.Reads) >= reads + 2, 4000));
                Assert.AreEqual(2, events.Count);
            }
            finally { MyHub.Unsubscribe(subscription.Token); }
        }

        private sealed class TimestampThing : Thing
        {
            public volatile Status<int> Current = new Status<int>(1);
            public int Reads;
            [Cfet2Status]
            public Status<int> Value { get { Interlocked.Increment(ref Reads); return Current; } }
        }
    }
}

#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize]
    public class ContainerObservationTests : CFET2Host
    {
        private int previousInterval;
        private static readonly Type Snapshot = typeof(Hub).Assembly.GetType("Jtext103.CFET2.Core.Event.ObservationValueSnapshot");
        private static object Capture(object value) => Snapshot.GetMethod("Capture", new[] { typeof(object) }).Invoke(null, new[] { value });
        private static bool Equal(object left, object right) => (bool)Snapshot.GetMethod("ContentEquals").Invoke(null, new[] { left, right });

        [TestInitialize]
        public void Init()
        {
            previousInterval = GlobalConfig.ResourceObservationIntervalMs;
            GlobalConfig.ResourceObservationIntervalMs = 20;
            HubMaster.InjectHubToModule(this);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Hub.KillMaster();
            GlobalConfig.ResourceObservationIntervalMs = previousInterval;
        }

        [TestMethod]
        public void NestedSnapshotsPreserveTypesAndDoNotAliasOriginalContainers()
        {
            var source = new Dictionary<string, List<int[]>> { ["a"] = new List<int[]> { new[] { 1, 2 } } };
            var copy = (Dictionary<string, List<int[]>>)Capture(source);
            Assert.IsTrue(Equal(source, copy));
            Assert.AreNotSame(source, copy);
            Assert.AreNotSame(source["a"], copy["a"]);
            Assert.AreNotSame(source["a"][0], copy["a"][0]);
            source["a"][0][0] = 9;
            Assert.AreEqual(1, copy["a"][0][0]);
            Assert.IsFalse(Equal(source, copy));
        }

        [TestMethod]
        public void ArraysCompareShapeBoundsOrderAndNestedNulls()
        {
            var matrix = new[,] { { 1, 2 }, { 3, 4 } };
            var copy = (int[,])Capture(matrix);
            Assert.IsTrue(Equal(matrix, copy));
            matrix[1, 0] = 5;
            Assert.IsFalse(Equal(matrix, copy));
            Assert.IsFalse(Equal(copy, new[,] { { 1, 2, 3, 4 } }));
            var bounded = Array.CreateInstance(typeof(int), new[] { 2 }, new[] { -1 });
            bounded.SetValue(7, -1);
            var boundedCopy = (Array)Capture(bounded);
            Assert.AreEqual(-1, boundedCopy.GetLowerBound(0));
            Assert.IsTrue(Equal(bounded, boundedCopy));
            Assert.IsFalse(Equal(bounded, Array.CreateInstance(typeof(int), new[] { 2 }, new[] { 1 })));
            Assert.IsTrue(Equal(Capture(new int[0, 2]), new int[0, 2]));
            Assert.IsFalse(Equal(new[] { 1, 2 }, new[] { 2, 1 }));
            Assert.IsTrue(Equal(Capture(new int[][] { null, new[] { 1 } }), new int[][] { null, new[] { 1 } }));
            Assert.IsFalse(Equal(new int[][] { null }, new int[][] { new int[0] }));
        }

        [TestMethod]
        public void ListsCompareLengthOrderAndNestedValues()
        {
            Assert.IsTrue(Equal(Capture(new List<string> { null, "a" }), new List<string> { null, "a" }));
            Assert.IsFalse(Equal(new List<int> { 1, 2 }, new List<int> { 2, 1 }));
            Assert.IsFalse(Equal(new List<int> { 1 }, new List<int> { 1, 2 }));
            Assert.IsTrue(Equal(new List<int>(), new List<int>()));
        }

        [TestMethod]
        public void DictionariesIgnoreInsertionOrderAndPreserveKeyComparer()
        {
            var source = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase) { ["a"] = new[] { 1 }, ["b"] = null };
            var copy = (Dictionary<string, int[]>)Capture(source);
            Assert.AreSame(source.Comparer, copy.Comparer);
            Assert.IsTrue(copy.ContainsKey("A"));
            var reordered = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase) { ["B"] = null, ["A"] = new[] { 1 } };
            Assert.IsTrue(Equal(copy, reordered));
            reordered["A"][0] = 2;
            Assert.IsFalse(Equal(copy, reordered));
            Assert.IsFalse(Equal(copy, new Dictionary<string, int[]> { ["a"] = new[] { 1 } }));
            var insensitive = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a"] = 1 };
            var sensitive = new Dictionary<string, int>(StringComparer.Ordinal) { ["A"] = 1 };
            Assert.IsFalse(Equal(insensitive, sensitive));
            Assert.IsFalse(Equal(sensitive, insensitive));
            Assert.IsTrue(Equal(new Dictionary<int, string> { [1] = "a" }, Capture(new Dictionary<int, string> { [1] = "a" })));
            Assert.IsTrue(Equal(new Dictionary<int?, string> { [1] = "a" }, Capture(new Dictionary<int?, string> { [1] = "a" })));
        }

        [TestMethod]
        public void InternalPublicationReportsAdmissionAndRefusesDisposedHub()
        {
            using var hub = new EventHub();
            var publish = typeof(EventHub).GetMethod("TryPublish", BindingFlags.Instance | BindingFlags.NonPublic);
            var args = new EventArg("/value", "changed", new Status<int[]>(new[] { 1 }));
            Assert.IsFalse((bool)publish.Invoke(hub, new object[] { null }));
            Assert.IsTrue((bool)publish.Invoke(hub, new object[] { args }));
            var pending = (ConcurrentQueue<EventArg>)typeof(EventHub).GetField("pendingLocalEvent", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hub);
            Assert.AreEqual(1, pending.Count);
            hub.Dispose();
            Assert.IsFalse((bool)publish.Invoke(hub, new object[] { args }));
            Assert.AreEqual(0, pending.Count);
        }

        [TestMethod]
        public void UnsupportedRuntimeContainersAndCyclesCannotBeCaptured()
        {
            var cyclic = new List<object>();
            cyclic.Add(cyclic);
            foreach (var value in new object[] { cyclic, new DerivedList(), new object[] { new object() }, new Dictionary<object, int>() })
                Assert.ThrowsExactly<TargetInvocationException>(() => Capture(value));
        }

        [TestMethod]
        public void ArrayInPlaceMutationPublishesStableValueAndSubscriberCannotChangeBaseline()
        {
            var thing = new ContainerThing<int[]> { Data = new[] { 1, 2 } };
            MyHub.TryAddThing(thing, "/", "array");
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/array/Value", events.Enqueue);
            thing.Data[0] = 3;
            Eventually(() => events.Count == 1);
            var first = (int[])events.ToArray()[0].Sample.ObjectVal;
            CollectionAssert.AreEqual(new[] { 3, 2 }, first);
            Assert.AreNotSame(thing.Data, first);
            first[0] = 99;
            AssertNoMoreEvents(thing, events, 1);
            thing.Data[0] = 4;
            Eventually(() => events.Count == 2);
            Assert.AreEqual(99, first[0]);
            CollectionAssert.AreEqual(new[] { 4, 2 }, (int[])events.ToArray()[1].Sample.ObjectVal);
        }

        [TestMethod]
        public void NewEqualListsDoNotPublishButOrderAddRemoveAndNullTransitionsDo()
        {
            var thing = new ContainerThing<List<int>> { Data = new List<int> { 1, 2 } };
            MyHub.TryAddThing(thing, "/", "list");
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/list/Value", events.Enqueue);
            thing.Data = new List<int> { 1, 2 };
            AssertNoMoreEvents(thing, events, 0);
            thing.Data.Reverse();
            Eventually(() => events.Count == 1);
            thing.Data.Add(3);
            Eventually(() => events.Count == 2);
            thing.Data.RemoveAt(0);
            Eventually(() => events.Count == 3);
            thing.Data = null;
            Eventually(() => events.Count == 4);
            Assert.IsNull(events.ToArray()[3].Sample.ObjectVal);
            thing.Data = new List<int>();
            Eventually(() => events.Count == 5);
            CollectionAssert.AreEqual(new[] { 2, 1 }, (List<int>)events.ToArray()[0].Sample.ObjectVal);
        }

        [TestMethod]
        public void DictionaryReorderDoesNotPublishButNestedMutationAndKeyChangesDo()
        {
            var thing = new ContainerThing<Dictionary<string, List<int>>> {
                Data = new Dictionary<string, List<int>> { ["a"] = new List<int> { 1 }, ["b"] = null } };
            MyHub.TryAddThing(thing, "/", "dictionary");
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/dictionary/Value", events.Enqueue);
            thing.Data = new Dictionary<string, List<int>> { ["b"] = null, ["a"] = new List<int> { 1 } };
            AssertNoMoreEvents(thing, events, 0);
            thing.Data["a"][0] = 2;
            Eventually(() => events.Count == 1);
            thing.Data.Remove("b");
            thing.Data["c"] = null;
            Eventually(() => events.Count == 2);
            Assert.AreEqual(2, ((Dictionary<string, List<int>>)events.ToArray()[0].Sample.ObjectVal)["a"][0]);
        }

        [TestMethod]
        public void RuntimeSnapshotFailureKeepsBaselineAndRecovers()
        {
            var thing = new ContainerThing<List<int>> { Data = new List<int> { 1 } };
            MyHub.TryAddThing(thing, "/", "recover");
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/recover/Value", events.Enqueue);
            thing.Data = new DerivedList { 2 };
            AssertNoMoreEvents(thing, events, 0);
            thing.Data = new List<int> { 1 };
            AssertNoMoreEvents(thing, events, 0);
            thing.Data[0] = 2;
            Eventually(() => events.Count == 1);
        }

        [TestMethod]
        public void InitialSnapshotFailureReportsFailedWithoutHandle()
        {
            var thing = new ContainerThing<List<int>> { Data = new DerivedList { 1 } };
            MyHub.TryAddThing(thing, "/", "failure");
            var result = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/failure/Value"));
            Assert.AreEqual(ResourceObservationStatus.Failed, result.Status);
            Assert.IsNull(result.Handle);
            StringAssert.Contains(result.Reason, "Unsupported automatic observation value");
            Assert.ThrowsExactly<Jtext103.CFET2.Core.Exception.BadResourceRequestException>(
                () => MyHub.SubscribeResource("/failure/Value", _ => { }));
        }

        [TestMethod]
        public void DelayedHandlerSeesPublicationValueAfterBusinessObjectChanges()
        {
            var thing = new ContainerThing<int[]> { Data = new[] { 1 } };
            MyHub.TryAddThing(thing, "/", "delayed");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            EventArg first = null;
            using var subscription = MyHub.SubscribeResource("/delayed/Value", e => {
                if (Interlocked.CompareExchange(ref first, e, null) == null)
                {
                    entered.Set();
                    release.Wait(3000);
                }
            });
            try
            {
                thing.Data[0] = 2;
                Assert.IsTrue(entered.Wait(3000));
                thing.Data[0] = 3;
                int reads = Volatile.Read(ref thing.Reads);
                Eventually(() => Volatile.Read(ref thing.Reads) > reads + 2);
                CollectionAssert.AreEqual(new[] { 2 }, (int[])first.Sample.ObjectVal);
            }
            finally { release.Set(); }
        }

        [TestMethod]
        public void WrappedConfigContainerPreservesSourceTimestamp()
        {
            var thing = new WrappedContainerThing();
            MyHub.TryAddThing(thing, "/", "wrappedContainer");
            var events = new ConcurrentQueue<EventArg>();
            using var subscription = MyHub.SubscribeResource("/wrappedContainer/Value", events.Enqueue);
            thing.Value.Val[0] = 2;
            Eventually(() => events.Count == 1);
            Assert.AreEqual(thing.Value.Timestamp, events.ToArray()[0].Sample.Timestamp);
            CollectionAssert.AreEqual(new[] { 2 }, (List<int>)events.ToArray()[0].Sample.ObjectVal);
            Assert.AreNotSame(thing.Value.Val, events.ToArray()[0].Sample.ObjectVal);
        }

        [TestMethod]
        public void RegexAndIndependentHandlesShareContainerObservation()
        {
            var thing = new ContainerThing<int[]> { Data = new[] { 1 } };
            MyHub.TryAddThing(thing, "/", "shared");
            var events = new ConcurrentQueue<EventArg>();
            using var group = MyHub.SubscribeResources(new EventFilter("/shared/.*", "changed"), events.Enqueue);
            Assert.AreEqual(ResourceObservationStatus.Started, group.ObservationResults[0].Status);
            var shared = MyHub.AcquireResourceObservation(MyHub.GetLocalResouce("/shared/Value"));
            Assert.AreEqual(ResourceObservationStatus.Shared, shared.Status);
            using (shared.Handle)
            {
                thing.Data[0] = 2;
                Eventually(() => events.Count == 1);
            }
            thing.Data[0] = 3;
            Eventually(() => events.Count == 2);
        }

        private static void Eventually(Func<bool> predicate) => Assert.IsTrue(SpinWait.SpinUntil(predicate, 3000));
        private static void AssertNoMoreEvents<T>(ContainerThing<T> thing, ConcurrentQueue<EventArg> events, int count)
        {
            int reads = Volatile.Read(ref thing.Reads);
            Eventually(() => Volatile.Read(ref thing.Reads) >= reads + 4);
            Thread.Sleep(30); // Allow the last read's event dispatch to finish.
            Assert.AreEqual(count, events.Count);
        }

        public class ContainerThing<T> : Thing
        {
            public T Data;
            public int Reads;
            [Cfet2Status] public T Value { get { Interlocked.Increment(ref Reads); return Data; } }
        }
        public class DerivedList : List<int> { }
        public class WrappedContainerThing : Thing
        {
            [Cfet2Config] public Config<List<int>> Value { get; set; } = new Config<List<int>>(new List<int> { 1 })
            { Timestamp = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero) };
        }
    }
}

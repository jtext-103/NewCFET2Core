#nullable disable
using System;
using System.Collections.Generic;
using Jtext103.CFET2.Core.Event;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class ScalarArraySnapshotTests
    {
        private enum Code : long { First = -1, Second = long.MaxValue }

        [TestMethod]
        public void ScalarAndNullableArraysPreserveValuesAndExactRuntimeType()
        {
            var cases = new Array[] {
                new[] { true, false }, new[] { '\0', '\uffff' },
                new byte[] { 0, 255 }, new sbyte[] { -128, 127 },
                new short[] { short.MinValue, short.MaxValue }, new ushort[] { 0, ushort.MaxValue },
                new[] { int.MinValue, int.MaxValue }, new uint[] { 0, uint.MaxValue },
                new[] { long.MinValue, long.MaxValue }, new ulong[] { 0, ulong.MaxValue },
                new[] { float.NaN, -0f, float.PositiveInfinity },
                new[] { double.NaN, -0d, double.NegativeInfinity },
                new[] { decimal.MinValue, 1.00m, decimal.MaxValue },
                new string[] { null, "", "value" }, new[] { Code.First, Code.Second },
                new[] { DateTime.MinValue, DateTime.UtcNow },
                new[] { DateTimeOffset.MinValue, DateTimeOffset.Now },
                new[] { TimeSpan.MinValue, TimeSpan.MaxValue }, new[] { Guid.Empty, Guid.NewGuid() },
                new int?[] { null, int.MinValue, 42 }, new double?[] { double.NaN, null, -0d },
                new Code?[] { null, Code.Second }, new Guid?[] { Guid.NewGuid(), null },
                new decimal?[] { null, 1.00m } };
            foreach (var source in cases)
            {
                var copy = (Array)ObservationValueSnapshot.Capture(source);
                Assert.AreEqual(source.GetType(), copy.GetType());
                Assert.AreNotSame(source, copy);
                CollectionAssert.AreEqual(source, copy);
                Assert.IsTrue(ObservationValueSnapshot.ContentEquals(source, copy));
                var saved = copy.GetValue(0);
                source.SetValue(null, 0);
                Assert.AreEqual(saved, copy.GetValue(0), copy.GetType().ToString());
            }
        }

        [TestMethod]
        public void CopiesPreserveFloatingPointBitsAndDecimalRepresentation()
        {
            var values = new[] { BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000042)), -0d };
            var copy = (double[])ObservationValueSnapshot.Capture(values);
            for (int index = 0; index < values.Length; index++)
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(values[index]), BitConverter.DoubleToInt64Bits(copy[index]));
            var decimals = new[] { 1.00m, new decimal(0, 0, 0, true, 3) };
            var decimalCopy = (decimal[])ObservationValueSnapshot.Capture(decimals);
            for (int index = 0; index < decimals.Length; index++)
                CollectionAssert.AreEqual(decimal.GetBits(decimals[index]), decimal.GetBits(decimalCopy[index]));
        }

        [TestMethod]
        public void MultidimensionalNonzeroBoundAndEmptyArraysKeepTheirShape()
        {
            foreach (var element in new[] { typeof(int), typeof(int?), typeof(string), typeof(Code) })
            {
                var source = Array.CreateInstance(element, new[] { 2, 3, 2 }, new[] { -2, 4, 1 });
                object value = element == typeof(string) ? "text" : element == typeof(Code) ? Code.Second : 17;
                source.SetValue(value, -1, 6, 2);
                var copy = (Array)ObservationValueSnapshot.Capture(source);
                Assert.AreEqual(source.GetType(), copy.GetType());
                Assert.AreEqual(3, copy.Rank);
                for (int dimension = 0; dimension < 3; dimension++)
                {
                    Assert.AreEqual(source.GetLowerBound(dimension), copy.GetLowerBound(dimension));
                    Assert.AreEqual(source.GetLength(dimension), copy.GetLength(dimension));
                }
                Assert.IsTrue(ObservationValueSnapshot.ContentEquals(source, copy));
                source.SetValue(null, -1, 6, 2);
                Assert.AreEqual(value, copy.GetValue(-1, 6, 2));
            }
            foreach (var source in new Array[] { new int[0], new string[0, 2],
                Array.CreateInstance(typeof(decimal?), new[] { 0, 2 }, new[] { -3, 5 }),
                Array.CreateInstance(typeof(int), new[] { 2 }, new[] { -1 }) })
            {
                var copy = (Array)ObservationValueSnapshot.Capture(source);
                Assert.AreNotSame(source, copy);
                Assert.AreEqual(source.GetType(), copy.GetType());
                Assert.IsTrue(ObservationValueSnapshot.ContentEquals(source, copy));
            }
        }

        [TestMethod]
        public void NestedContainersStillCopyEveryMutableArrayAndPreserveComparer()
        {
            var shared = new int?[] { 1, null };
            var source = new Dictionary<string, List<int?[]>>(StringComparer.OrdinalIgnoreCase) {
                ["Key"] = new List<int?[]> { shared, shared, null } };
            var copy = (Dictionary<string, List<int?[]>>)ObservationValueSnapshot.Capture(source);
            Assert.AreSame(source.Comparer, copy.Comparer);
            Assert.AreNotSame(shared, copy["KEY"][0]);
            Assert.AreNotSame(copy["KEY"][0], copy["KEY"][1]);
            shared[0] = 9;
            Assert.AreEqual(1, copy["key"][0][0]);
            copy["key"][0][0] = 7;
            Assert.AreEqual(1, copy["key"][1][0]);
            Assert.IsNull(copy["key"][2]);
            var jagged = new[] { new[] { "original" }, null };
            var jaggedCopy = (string[][])ObservationValueSnapshot.Capture(jagged);
            jagged[0][0] = "changed";
            Assert.AreEqual("original", jaggedCopy[0][0]);
            Assert.IsNull(jaggedCopy[1]);
        }

        [TestMethod]
        public void SourceBaselineAndEventArraysRemainIndependent()
        {
            var source = new int?[,] { { 1, null }, { 3, 4 } };
            var baseline = ResourceValueComparison.Capture(source);
            var eventValue = (int?[,])baseline.CreateEventValue();
            source[0, 0] = 8;
            Assert.AreEqual(1, eventValue[0, 0]);
            Assert.IsTrue(ResourceValueComparison.HasChanged(baseline, ResourceValueComparison.Capture(source)));
            eventValue[1, 1] = 99;
            var laterEventValue = (int?[,])baseline.CreateEventValue();
            Assert.AreEqual(4, laterEventValue[1, 1]);
            Assert.AreNotSame(eventValue, laterEventValue);
            Assert.IsFalse(ResourceValueComparison.HasChanged(baseline,
                ResourceValueComparison.Capture(new int?[,] { { 1, null }, { 3, 4 } })));
        }

        [TestMethod]
        public void ArrayComparisonRetainsNaNZeroTypeAndShapeRules()
        {
            Assert.IsTrue(ObservationValueSnapshot.ContentEquals(new[] { double.NaN, 0d }, new[] { double.NaN, -0d }));
            Assert.IsFalse(ObservationValueSnapshot.ContentEquals(new[] { double.NaN }, new[] { 1d }));
            Assert.IsFalse(ObservationValueSnapshot.ContentEquals(new[] { 1 }, new[] { 1L }));
            Assert.IsFalse(ObservationValueSnapshot.ContentEquals(new[] { 1d }, new[] { 1m }));
            Assert.IsFalse(ObservationValueSnapshot.ContentEquals(new int[1, 2], new int[2, 1]));
        }

        [TestMethod]
        public void UnsupportedElementTypesAndCyclesRemainRejected()
        {
            foreach (var value in new object[] { new object[0], new object[] { "scalar at runtime" },
                new MutableStruct[0], new DateOnly[0], new List<object>[0], new Array[0] })
                Assert.ThrowsExactly<NotSupportedException>(() => ObservationValueSnapshot.Capture(value));
            var cycle = new object[1];
            cycle[0] = cycle;
            Assert.ThrowsExactly<NotSupportedException>(() => ObservationValueSnapshot.Capture(cycle));
        }

        private struct MutableStruct { public int[] Values; }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;

namespace Jtext103.CFET2.Core.Event
{
    // Snapshots contain only immutable scalar leaves and independent supported containers.
    internal static class ObservationValueSnapshot
    {
        public static object Capture(object value)
        {
            if (value == null) return null;
            if (ResourceObservationTypePolicy.IsScalar(value.GetType())) return value;
            return Capture(value, new HashSet<object>(ReferenceComparer.Instance), "Value");
        }

        private static object Capture(object value, HashSet<object> ancestors, string location)
        {
            if (value == null) return null;
            var type = value.GetType();
            ValueTypeValidation.EnsureSupported(type, location);
            if (ResourceObservationTypePolicy.IsScalar(type)) return value;
            if (!ancestors.Add(value))
                throw new InvalidOperationException("Cyclic automatic observation value.");
            try
            {
                if (value is Array array)
                {
                    // Clone preserves rank, lower bounds and the exact runtime array type.
                    var copy = (Array)array.Clone();
                    var elementType = type.GetElementType();
                    if (ResourceObservationTypePolicy.IsScalar(Nullable.GetUnderlyingType(elementType) ?? elementType))
                        return copy;
                    foreach (var indices in Indices(array))
                        copy.SetValue(Capture(array.GetValue(indices), ancestors, location + ".Array[" + string.Join(",", indices) + "]"), indices);
                    return copy;
                }
                if (value is IDictionary dictionary)
                {
                    var comparer = type.GetProperty("Comparer").GetValue(value);
                    var comparerType = typeof(IEqualityComparer<>).MakeGenericType(type.GetGenericArguments()[0]);
                    var copy = (IDictionary)type.GetConstructor(new[] { comparerType }).Invoke(new[] { comparer });
                    foreach (DictionaryEntry entry in dictionary)
                        copy.Add(entry.Key, Capture(entry.Value, ancestors, location + ".Dictionary.Value"));
                    return copy;
                }
                var list = (IList)value;
                var listCopy = (IList)Activator.CreateInstance(type);
                foreach (var item in list) listCopy.Add(Capture(item, ancestors, location + ".List[" + listCopy.Count + "]"));
                return listCopy;
            }
            finally { ancestors.Remove(value); }
        }

        public static bool ContentEquals(object left, object right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.GetType() != right.GetType()) return false;
            if (left is Array leftArray)
            {
                var rightArray = (Array)right;
                if (leftArray.Rank != rightArray.Rank) return false;
                for (int dimension = 0; dimension < leftArray.Rank; dimension++)
                    if (leftArray.GetLength(dimension) != rightArray.GetLength(dimension) ||
                        leftArray.GetLowerBound(dimension) != rightArray.GetLowerBound(dimension)) return false;
                foreach (var indices in Indices(leftArray))
                    if (!ContentEquals(leftArray.GetValue(indices), rightArray.GetValue(indices))) return false;
                return true;
            }
            if (left is IDictionary leftDictionary)
            {
                var rightDictionary = (IDictionary)right;
                return leftDictionary.Count == rightDictionary.Count &&
                    DictionaryEquals(leftDictionary, rightDictionary) && DictionaryEquals(rightDictionary, leftDictionary);
            }
            if (left is IList leftList)
            {
                var rightList = (IList)right;
                if (leftList.Count != rightList.Count) return false;
                for (int index = 0; index < leftList.Count; index++)
                    if (!ContentEquals(leftList[index], rightList[index])) return false;
                return true;
            }
            return object.Equals(left, right);
        }

        private static bool DictionaryEquals(IDictionary left, IDictionary right)
        {
            foreach (DictionaryEntry entry in left)
                if (!right.Contains(entry.Key) || !ContentEquals(entry.Value, right[entry.Key])) return false;
            return true;
        }

        private static IEnumerable<int[]> Indices(Array array)
        {
            if (array.Length == 0) yield break;
            var indices = new int[array.Rank];
            for (int dimension = 0; dimension < indices.Length; dimension++)
                indices[dimension] = array.GetLowerBound(dimension);
            while (true)
            {
                yield return indices;
                int dimension = indices.Length - 1;
                while (dimension >= 0 && indices[dimension] == array.GetUpperBound(dimension))
                {
                    indices[dimension] = array.GetLowerBound(dimension);
                    dimension--;
                }
                if (dimension < 0) yield break;
                indices[dimension]++;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }
}

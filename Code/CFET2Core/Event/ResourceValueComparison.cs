using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jtext103.CFET2.Core.Event
{
    /// <summary>An independent comparison baseline. Mutable values are never exposed.</summary>
    public sealed class ResourceValueSnapshot
    {
        internal ResourceValueSnapshot(object value) { Value = value; }
        internal object Value { get; }
        internal object CreateEventValue() => ObservationValueSnapshot.Capture(Value);
    }

    /// <summary>Captures and compares supported scalar and container resource values.</summary>
    public static class ResourceValueComparison
    {
        public static ResourceValueSnapshot Capture<T>(T value) => Capture(typeof(T), value);

        public static bool HasChanged(ResourceValueSnapshot previous, ResourceValueSnapshot current)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            if (current == null) throw new ArgumentNullException(nameof(current));
            return !ObservationValueSnapshot.ContentEquals(previous.Value, current.Value);
        }

        internal static ResourceValueSnapshot Capture(Type declaredType, object value)
        {
            ValueTypeValidation.EnsureSupported(declaredType, "DeclaredType");
            return new ResourceValueSnapshot(ObservationValueSnapshot.Capture(value));
        }
    }

    internal static class ValueTypeValidation
    {
        internal sealed class Result
        {
            internal Result(Type unsupported = null, string location = "") { Unsupported = unsupported; Location = location; }
            internal Type Unsupported { get; }
            internal string Location { get; }
        }

        private static readonly ConcurrentDictionary<Type, Result> cache = new ConcurrentDictionary<Type, Result>();
        internal static Result Get(Type type) => type == null ? new Result(typeof(object), "Unknown") : cache.GetOrAdd(type, Inspect);
        internal static void EnsureSupported(Type type, string location)
        {
            var result = Get(type);
            if (result.Unsupported != null)
                throw new NotSupportedException($"Unsupported automatic observation value: '{type}', unsupported type '{result.Unsupported}' at {location}{result.Location}.");
        }

        private static Result Inspect(Type type)
        {
            var effective = Nullable.GetUnderlyingType(type) ?? type;
            if (ResourceObservationTypePolicy.IsScalar(effective)) return new Result();
            if (type.IsArray) return Child(type.GetElementType(), ".Array.Element");
            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var arguments = type.GetGenericArguments();
                if (definition == typeof(List<>)) return Child(arguments[0], ".List.Element");
                if (definition == typeof(Dictionary<,>))
                {
                    if (!ResourceObservationTypePolicy.IsScalar(Nullable.GetUnderlyingType(arguments[0]) ?? arguments[0]))
                        return new Result(arguments[0], ".Dictionary.Key");
                    return Child(arguments[1], ".Dictionary.Value");
                }
            }
            return new Result(type);
        }

        private static Result Child(Type type, string location)
        {
            var result = Get(type);
            return result.Unsupported == null ? result : new Result(result.Unsupported, location + result.Location);
        }
    }
}

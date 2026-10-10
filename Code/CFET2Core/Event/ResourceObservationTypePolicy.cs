using System;
using System.Collections.Generic;
using System.Reflection;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;

namespace Jtext103.CFET2.Core.Event
{
    internal static class ResourceObservationTypePolicy
    {
        public static void EnsureSupported(ResourceBase resource)
        {
            var declaredType = GetDeclaredType(resource);
            var valueType = GetValueType(resource);
            if (SupportsValue(valueType)) return;
            throw new WrongResourceActionException(
                $"Resource '{resource.Path}' with declared type '{declaredType}' " +
                $"does not support automatic observation (value type: '{valueType?.ToString() ?? "unknown"}'). " +
                "Return a supported scalar, array, List<T> or Dictionary<TKey, TValue>, or use [Cfet2Event] and publish changes explicitly from the Thing.");
        }

        private static Type GetDeclaredType(ResourceBase resource)
        {
            MemberInfo getter;
            resource.Info.Implementations.TryGetValue(AccessAction.get, out getter);
            return getter is PropertyInfo property
                ? property.PropertyType
                : (getter as MethodInfo)?.ReturnType;
        }

        internal static Type GetValueType(ResourceBase resource)
        {
            var declaredType = GetDeclaredType(resource);
            var valueType = declaredType;

            if (declaredType != null && typeof(ISample).IsAssignableFrom(declaredType))
            {
                valueType = null;
                for (var type = declaredType; type != null; type = type.BaseType)
                {
                    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SampleBase<>))
                    {
                        valueType = type.GetGenericArguments()[0];
                        break;
                    }
                }
            }

            if (valueType != null)
                valueType = Nullable.GetUnderlyingType(valueType) ?? valueType;

            return valueType;
        }

        internal static bool SupportsValue(Type type)
        {
            return type != null && ValueTypeValidation.Get(type).Unsupported == null;
        }

        internal static bool IsScalar(Type type)
        {
            return type.IsEnum || type == typeof(bool) || type == typeof(char) ||
                type == typeof(byte) || type == typeof(sbyte) ||
                type == typeof(short) || type == typeof(ushort) ||
                type == typeof(int) || type == typeof(uint) ||
                type == typeof(long) || type == typeof(ulong) ||
                type == typeof(float) || type == typeof(double) || type == typeof(decimal) ||
                type == typeof(string) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
                type == typeof(TimeSpan) || type == typeof(Guid);
        }
    }
}

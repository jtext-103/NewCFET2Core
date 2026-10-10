using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Jtext103.CFET2.Core.Extension;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;
using Newtonsoft.Json;

namespace Jtext103.CFET2.Core
{
    public partial class Hub
    {
        private readonly HubMaster publicationMaster;

        internal bool PublishThingResourceIfChanged<T>(Thing thing, string resourceName, T value,
            object[] inputs, DateTimeOffset? timestamp)
        {
            publicationMaster.MyEventHub.EnsurePublicationOpen();
            if (!ReferenceEquals(thing.MyHub, this) || thing.Path == null ||
                !publicationMaster.Resources.TryGetValue(thing.Path, out var mounted) ||
                !(mounted is ResourceThing owner) || !ReferenceEquals(owner.TheThing, thing))
                throw new InvalidOperationException("Thing is not registered in this Hub.");
            if (string.IsNullOrWhiteSpace(resourceName)) throw new ArgumentException("Resource name is required.", nameof(resourceName));
            var declaredPath = thing.GetPathFor(resourceName);
            var declaredName = declaredPath.Substring(declaredPath.LastIndexOf('/') + 1);
            if (!owner.Resources.TryGetValue(declaredName, out var resource))
                throw new ArgumentException("Resource does not belong to this Thing: " + resourceName, nameof(resourceName));
            if (resource.ResourceType != ResourceTypes.Status && resource.ResourceType != ResourceTypes.Config)
                throw new ArgumentException("Only Status and Config can publish resource changes: " + resource.Path, nameof(resourceName));
            resource.Info.Implementations.TryGetValue(AccessAction.get, out var getter);
            var method = getter as MethodInfo;
            var specifications = method != null ? method.GetParameters() : (getter as PropertyInfo)?.GetIndexParameters() ?? new ParameterInfo[0];
            inputs = inputs ?? new object[0];
            bool variadic = specifications.Length > 0 && specifications.Last().IsDefined(typeof(ParamArrayAttribute), false);
            if (!variadic && inputs.Length > specifications.Length)
                throw new ArgumentException("Too many resource parameters: " + resource.Path, nameof(inputs));
            var mapped = MapPublicationInputs(inputs, specifications, variadic, resource.Path);
            var flat = Flatten(mapped, variadic);
            var segments = flat.Select(FormatPublicationParameter).ToArray();
            var path = resource.Path + (segments.Length == 0 ? "" : "/" + string.Join("/", segments));
            // Use the existing parser to verify addressing without invoking device code.
            try
            {
                var parsed = ParseLocalRequest(new Uri(path, UriKind.RelativeOrAbsolute));
                if (parsed.ResourcePath != resource.Path || parsed.InputDict != null || parsed.Inputs.Length != flat.Count)
                    throw new ArgumentException("Parameters cannot be represented by the existing Get path rules.");
                var roundTrip = Flatten(MapPublicationInputs(parsed.Inputs, specifications, variadic, resource.Path), variadic);
                if (!segments.SequenceEqual(roundTrip.Select(FormatPublicationParameter)))
                    throw new ArgumentException("Parameters do not round-trip through Get path rules.");
            }
            catch (System.Exception ex) { throw new ArgumentException("Invalid parameterized resource path: " + path, nameof(inputs), ex); }
            return publicationMaster.MyEventHub.PublishIfChanged(resource, path, value, timestamp);
        }

        private static object[] MapPublicationInputs(object[] inputs, ParameterInfo[] specifications, bool variadic, string path)
        {
            int fixedCount = variadic ? specifications.Length - 1 : specifications.Length;
            var normalized = inputs.ToList();
            for (int i = normalized.Count; i < fixedCount; i++)
            {
                if (!specifications[i].HasDefaultValue) throw new ArgumentException("Missing parameter '" + specifications[i].Name + "' for " + path);
                normalized.Add(specifications[i].DefaultValue);
            }
            try
            {
                if (variadic && normalized.Count == fixedCount)
                {
                    var fixedValues = normalized.ToArray().MapInputParameters(specifications.Take(fixedCount).ToArray());
                    return fixedValues.Concat(new object[] { Array.CreateInstance(specifications.Last().ParameterType.GetElementType(), 0) }).ToArray();
                }
                return normalized.ToArray().MapInputParameters(specifications);
            }
            catch (System.Exception ex) { throw new ArgumentException("Invalid resource parameters for " + path, nameof(inputs), ex); }
        }

        private static List<object> Flatten(object[] values, bool variadic)
        {
            var result = values.Take(variadic ? values.Length - 1 : values.Length).ToList();
            if (variadic) foreach (var item in (Array)values.Last()) result.Add(item);
            return result;
        }

        private static string FormatPublicationParameter(object value)
        {
            if (value == null) throw new ArgumentException("Null resource parameters cannot be represented as path segments.");
            var text = value is string str ? str : value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture) : value is bool boolean
                ? boolean.ToString() : JsonConvert.SerializeObject(value);
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("Empty resource parameters cannot be represented as path segments.");
            return text;
        }
    }
}

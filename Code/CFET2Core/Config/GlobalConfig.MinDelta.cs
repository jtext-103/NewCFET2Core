using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jtext103.CFET2.Core.Config
{
    public static partial class GlobalConfig
    {
        private static double resourceMinDelta;
        private static IReadOnlyList<ResourceMinDeltaOverride> resourceMinDeltaOverrides = ResourceMinDeltaOverride.Copy(null);

        /// <summary>Threshold for the next Core. Existing EventHubs retain their snapshot.</summary>
        public static double ResourceMinDelta
        {
            get => resourceMinDelta;
            set { ResourceMinDeltaOverride.Validate(value, nameof(ResourceMinDelta)); resourceMinDelta = value == 0 ? 0 : value; }
        }
        public static IReadOnlyList<ResourceMinDeltaOverride> ResourceMinDeltaOverrides
        {
            get => resourceMinDeltaOverrides;
            set => resourceMinDeltaOverrides = ResourceMinDeltaOverride.Copy(value);
        }
        internal static ResourceChangePublicationConfiguration CaptureChangePublicationConfiguration() =>
            new ResourceChangePublicationConfiguration(resourceMinDelta, resourceMinDeltaOverrides);

        private static double ParseMinDelta(string raw, string location)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                value < 0 || double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("Invalid " + location + ": expected a finite nonnegative number.", location);
            return value == 0 ? 0 : value;
        }

        private static IReadOnlyList<ResourceMinDeltaOverride> ParseMinDeltaOverrides(Dictionary<string, string> config)
        {
            const string key = "ResourceMinDeltaOverrides";
            if (!config.TryGetValue(key, out var raw) || raw == null) return ResourceMinDeltaOverride.Copy(null);
            try
            {
                var token = JToken.Parse(raw);
                if (token.Type == JTokenType.Null) return ResourceMinDeltaOverride.Copy(null);
                if (!(token is JArray array)) throw new ArgumentException("Expected an array.");
                var items = new List<ResourceMinDeltaOverride>();
                var paths = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                for (int i = 0; i < array.Count; i++)
                {
                    var location = key + "[" + i + "]";
                    if (!(array[i] is JObject item)) throw new ArgumentException(location + ": expected an object.");
                    foreach (var property in item.Properties())
                        if (property.Name != "Path" && property.Name != "MinDelta") throw new ArgumentException(location + ": unknown field " + property.Name);
                    if (item["Path"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)item["Path"]))
                        throw new ArgumentException(location + ".Path: expected a nonempty path.");
                    var path = (string)item["Path"];
                    if (!paths.Add(path)) throw new ArgumentException(location + ".Path: duplicate path " + path);
                    var delta = item["MinDelta"];
                    if (delta == null || (delta.Type != JTokenType.Float && delta.Type != JTokenType.Integer))
                        throw new ArgumentException(location + ".MinDelta: expected a number.");
                    items.Add(new ResourceMinDeltaOverride(path, ParseMinDelta(delta.ToString(Formatting.None), location + ".MinDelta")));
                }
                return ResourceMinDeltaOverride.Copy(items);
            }
            catch (System.Exception ex) when (ex is ArgumentException || ex is JsonException || ex is OverflowException)
            { throw new ArgumentException("Invalid " + key + ": " + ex.Message, key, ex); }
        }
    }
}

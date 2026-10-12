using System;
using System.Collections.Generic;
using Jtext103.CFET2.Core.Resource;

namespace Jtext103.CFET2.Core.Config
{
    /// <summary>An immutable threshold override for a local base resource path.</summary>
    public sealed class ResourceMinDeltaOverride
    {
        public string Path { get; }
        public double MinDelta { get; }
        public ResourceMinDeltaOverride(string path, double minDelta)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Resource path is required.", nameof(path));
            Validate(minDelta, nameof(minDelta));
            Path = path;
            MinDelta = minDelta == 0 ? 0 : minDelta;
        }

        internal static void Validate(double value, string parameter)
        {
            if (value < 0 || double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameter, "Threshold must be finite and nonnegative.");
        }

        internal static IReadOnlyList<ResourceMinDeltaOverride> Copy(IEnumerable<ResourceMinDeltaOverride> source)
        {
            var items = new List<ResourceMinDeltaOverride>();
            var paths = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            if (source != null)
                foreach (var item in source)
                {
                    if (item == null) throw new ArgumentException("Null threshold override at index " + items.Count);
                    if (!paths.Add(item.Path)) throw new ArgumentException("Duplicate threshold path: " + item.Path);
                    items.Add(item);
                }
            return items.AsReadOnly();
        }
    }

    internal sealed class ResourceChangePublicationConfiguration
    {
        private readonly double global;
        private readonly Dictionary<string, double> overrides = new Dictionary<string, double>(StringComparer.InvariantCultureIgnoreCase);
        internal ResourceChangePublicationConfiguration(double global, IEnumerable<ResourceMinDeltaOverride> items)
        {
            ResourceMinDeltaOverride.Validate(global, nameof(global));
            this.global = global;
            foreach (var item in ResourceMinDeltaOverride.Copy(items)) overrides.Add(item.Path, item.MinDelta);
        }
        internal double ResolveMinDelta(ResourceBase resource) => overrides.TryGetValue(resource.Path, out var value) ? value : global;
        internal IEnumerable<string> Paths => overrides.Keys;
    }
}

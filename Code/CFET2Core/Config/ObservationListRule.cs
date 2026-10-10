using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jtext103.CFET2.Core.Event;
using Newtonsoft.Json;

namespace Jtext103.CFET2.Core.Config
{
    /// <summary>A local resource selector. Specify exactly one of Path (literal) or Regex.</summary>
    public sealed class ObservationListRule
    {
        [JsonConstructor]
        public ObservationListRule(string path = null, string regex = null)
        {
            if ((path == null) == (regex == null))
                throw new ArgumentException("Specify exactly one of Path or Regex.");
            if (path != null && (!path.StartsWith("/", StringComparison.Ordinal) ||
                path.Contains("://") || path.Contains("?") || path.Contains("#")))
                throw new ArgumentException("Path must be a local registered resource path without query or fragment.");
            if (regex != null && (string.IsNullOrWhiteSpace(regex) || regex.Contains("://")))
                throw new ArgumentException("Regex must be a nonempty local resource expression.");
            Path = path;
            Regex = regex;
            Compile();
        }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Path { get; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Regex { get; }

        internal EventFilter Compile()
        {
            var filter = new EventFilter();
            filter.AddSourceAndType(Path == null ? Regex : System.Text.RegularExpressions.Regex.Escape(Path), "changed");
            return filter;
        }

        public override string ToString() => Path == null ? "Regex: " + Regex : "Path: " + Path;
    }

    /// <summary>Immutable configured or active lists. A running Hub retains its startup snapshot.</summary>
    public sealed class AutomaticObservationConfiguration
    {
        internal AutomaticObservationConfiguration(IEnumerable<ObservationListRule> observe, IEnumerable<ObservationListRule> deny)
        {
            AutoObserveList = Copy(observe);
            AutoDenyList = Copy(deny);
        }

        internal static IReadOnlyList<ObservationListRule> Copy(IEnumerable<ObservationListRule> rules)
        {
            var copy = (rules ?? Enumerable.Empty<ObservationListRule>()).ToArray();
            if (copy.Any(rule => rule == null)) throw new ArgumentException("Observation lists cannot contain null entries.");
            return Array.AsReadOnly(copy);
        }

        public IReadOnlyList<ObservationListRule> AutoObserveList { get; }
        public IReadOnlyList<ObservationListRule> AutoDenyList { get; }
    }
}

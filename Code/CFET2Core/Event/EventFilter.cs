using Jtext103.CFET2.Core.Communication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jtext103.CFET2.Core.Event
{
    /// <summary>
    /// a event filter to select the event you have subscribed
    /// </summary>
    public  class EventFilter
    {
        public static string DefaultEventType="Changed";
        /// <summary>
        /// a collection of  SourceAndTypeFilter, any mathch will fire the handler, donot add to list!! it will not work use AddSourceAndTpye method!!
        /// </summary>
        public List<SourceAndTypeFilter> SourcesAndTypes { get; } = new List<SourceAndTypeFilter>();

        /// <summary>
        /// the remote host of the event publisher
        /// </summary>
        public string Host { get; set; } = "";

        /// <summary>
        /// only applicable for remote event, 0 for lowest full sample,, 1 for minimum overhead
        /// </summary>
        public int PerformanceLevel { get; set; } = 0;


        public static TimeSpan MatchTimeout { get; } = TimeSpan.FromMilliseconds(100);
        private List<Regex> resourceRegexMatches = new List<Regex>();
        private List<Regex> eventTypeRegexMatches = new List<Regex>();


        /// <summary>
        /// create an empty filter
        /// </summary>
        public EventFilter()
        {
            Host = "";
            PerformanceLevel = 0;
        }

        /// <summary>
        /// split string into two halves by splitStr. if keep true, will keep splitStr within the right side
        /// </summary>
        /// <param name="originStr"></param>
        /// <param name="splitStr"></param>
        /// <param name="keep"></param>
        /// <returns></returns>
        private (string, string) splitStringHalf(string originStr, string splitStr, bool keep)
        {
            int index = originStr.IndexOf(splitStr);
            if (index == -1) return (originStr, "");
            int index2 = keep?index:index+splitStr.Length;
            return (originStr.Substring(0, index), originStr.Substring(index2));
        }

        /// <summary>
        /// create a event filter with the input
        /// </summary>
        /// <param name="resource">IgnoreCase</param>
        /// <param name="eventType">IgnoreCase</param>
        public EventFilter(string resource, string eventType):this()
        {
            if (!resource.Contains("://"))
            {
                AddSourceAndType(resource, eventType);
                return;
            }

            if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri))
            {
                AddSourceAndType(resource, eventType);
                return;
            }

            if (uri.AbsolutePath == "/")
            {
                AddSourceAndType(resource, eventType);
            }
            else
            {
                AddSourceAndType(uri.PathAndQuery, eventType);
            }

            if (uri.Port < 0)
            {
                throw new FormatException("Remote event host must include a port or use a protocol with a default port.");
            }

            Host = HubMaster.getInstance().MyEventHub.GetFullHost(uri.Scheme, uri.Host, uri.Port);
        }
         
        public EventFilter(string resource, string eventType, int performanceLevel) : this(resource, eventType)
        {
            PerformanceLevel = performanceLevel;
        }

        /// <summary>
        /// create a event filter with the input
        /// </summary>
        public EventFilter(EventFilter oldFilter) : this()
        {
            if (oldFilter == null) throw new ArgumentNullException(nameof(oldFilter));
            SourcesAndTypes.AddRange(oldFilter.SourcesAndTypes.Select(item =>
                new SourceAndTypeFilter { Source = item.Source, EventType = item.EventType }));
            (resourceRegexMatches, eventTypeRegexMatches) = makeRegexFilter(SourcesAndTypes);
            Host = EventHub.NormalizeHost(oldFilter.Host);
            PerformanceLevel = oldFilter.PerformanceLevel;
        }

        /// <summary>
        /// create a event filter with the input
        /// </summary>
        /// <param name="resource">IgnoreCase</param>
        /// <param name="eventType">IgnoreCase</param>
        /// <param name="performanceLevel"></param>
        /// <param name="host">the remote host incluting protocol</param>
        public EventFilter(string resource, string eventType, int performanceLevel, string host) : this(resource, eventType)
        {
            Host = EventHub.NormalizeHost(host);
            PerformanceLevel = performanceLevel;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="resource"></param>
        /// <param name="eventType"></param>
        public void AddSourceAndType(string resource, string eventType)
        {
            // Compile before changing state: an invalid addition must not corrupt an existing filter.
            var filters = new List<SourceAndTypeFilter>(SourcesAndTypes)
            {
                new SourceAndTypeFilter { Source = resource, EventType = eventType }
            };
            var compiled = makeRegexFilter(filters);
            SourcesAndTypes.Add(filters[filters.Count - 1]);
            (resourceRegexMatches, eventTypeRegexMatches) = compiled;
        }


        /// <summary>
        /// returen if a event matches the filter
        /// </summary>
        /// <param name="eventArg">the event to filter</param>
        /// <returns>is this event match the event filter, if so it means it need to be handled</returns>
        public bool Predicate(EventArg eventArg)
        {
            return Matches(eventArg.Source, eventArg.EventType);
        }

        /// <summary>Uses the same full-match semantics for events and resource discovery. May throw RegexMatchTimeoutException.</summary>
        public bool Matches(string resource, string eventType)
        {

            int matchIndex = 0;
            for (matchIndex=0; matchIndex < resourceRegexMatches.Count(); matchIndex++)
            {
                var souResult = resourceRegexMatches[matchIndex].Match(resource);
                if (souResult.Success && souResult.Value == resource)
                {
                    //test event type
                    var eventTypeResult = eventTypeRegexMatches[matchIndex].Match(eventType);
                    if (eventTypeResult.Success && eventTypeResult.Value == eventType)
                    {
                        return true;
                    }
                }
            }
            return false;
        }



        private (List<Regex>, List<Regex>) makeRegexFilter(List<SourceAndTypeFilter> filters)
        {
            List<Regex> resources = new List<Regex>();
            List<Regex> eventTypes = new List<Regex>();
            foreach (var filter in filters)
            {
                resources.Add(new Regex(filter.Source, RegexOptions.IgnoreCase, MatchTimeout));
                eventTypes.Add(new Regex(filter.EventType, RegexOptions.IgnoreCase, MatchTimeout));
            }
            return (resources, eventTypes);
        }
    }
}

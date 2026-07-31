using Jtext103.CFET2.Core.Log;
using Jtext103.CFET2.Core.Sample;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jtext103.CFET2.Core.Event
{
    /// <summary>
    /// let things to subscribe and publish events
    /// the eventHub shouldn't be known, actually Publish/Sub/UnSub should be call like: MyHub.Publish
    /// </summary>
    public class EventHub : IDisposable
    {
        /// <summary>
        /// create the event
        /// </summary>
        public EventHub()
        {
            logger = Cfet2LogManager.GetLogger("EventHub");
        }

        CancellationTokenSource tokenSource = new CancellationTokenSource();
        CancellationToken cancelToken;

        /// <summary>
        /// init the event hub: start the event handling loop
        /// </summary>
        public void Init()
        {
            cancelToken = tokenSource.Token;
            Task.Factory.StartNew(() => eventHandleLocalLoop(), cancelToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task.Factory.StartNew(() => eventHandleRemoteLoop(), cancelToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// the ifinit loop of handling local event
        /// </summary>
        private void eventHandleLocalLoop()
        {
            while (true)
            {
                //block the handling loop untill a new event has come
                eventPendingLocalEvent.WaitOne();
                while (pendingLocalEvent.Count > 0)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        return;
                    }
                    EventArg eventArg;
                    if (!pendingLocalEvent.TryDequeue(out eventArg))
                    {
                        continue;
                    }
                    handleLocalEvent(eventArg);
                }
            }
        }

        private void eventHandleRemoteLoop()
        {
            while (true)
            {
                eventPendingRemoteEvent.WaitOne();
                while (pendingRemoteEvent.Count > 0)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        return;
                    }
                    EventArg eventArg;
                    if (!pendingRemoteEvent.TryDequeue(out eventArg))
                    {
                        continue;
                    }
                    handleRemoteEvent(eventArg);
                }
            }
        }

        private void handleLocalEvent(EventArg eventArg)
        {
            if (eventArg == null)
            {
                logger.Warn("local event args is null");
                return;
            }

            foreach (var candidate in getLocalCandidates(eventArg))
            {
                var subscriber = candidate.Value;
                if (tryMatchSubscriber(subscriber, eventArg))
                {
                    //fire and forget
                    Task.Run(() => invokeLocalHandlerIfSubscribed(candidate.Key, subscriber, eventArg));
                }
            }
        }

        private void handleRemoteEvent(EventArg eventArg)
        {
            if (eventArg == null)
            {
                logger.Warn("remote event args is null");
                return;
            }

            foreach (var candidate in getRemoteCandidates(eventArg))
            {
                var subscriber = candidate.Value;
                // if the host is same then check event type
                if(string.Equals(eventArg.Host,subscriber.Filter.Host, StringComparison.OrdinalIgnoreCase))
                {
                    if (tryMatchSubscriber(subscriber, eventArg))
                    {
                        //fire and forget
                        Task.Run(() => invokeRemoteHandlerIfSubscribed(candidate.Key, subscriber, eventArg));
                    }
                }
                else
                {
                    logger.Warn($"remote event host mismatch: event host {eventArg.Host}, filter host {subscriber.Filter.Host}");
                }
            }
        }

        /// <summary>
        /// dictionary for subscribes
        /// </summary>
        Dictionary<Guid, Subscriber> localSubDict { get; } = new Dictionary<Guid, Event.Subscriber>();
        Dictionary<Guid, Subscriber> remoteSubDict { get; } = new Dictionary<Guid, Event.Subscriber>();
        Dictionary<string, List<Guid>> localRouteTable { get; } = new Dictionary<string, List<Guid>>(StringComparer.InvariantCultureIgnoreCase);
        Dictionary<string, List<Guid>> remoteRouteTable { get; } = new Dictionary<string, List<Guid>>(StringComparer.InvariantCultureIgnoreCase);
        List<Guid> localFallbackRoutes { get; } = new List<Guid>();
        List<Guid> remoteFallbackRoutes { get; } = new List<Guid>();
        private readonly object localSubLock = new object();
        private readonly object remoteSubLock = new object();


        ConcurrentQueue<EventArg> pendingLocalEvent = new ConcurrentQueue<EventArg>();
        ConcurrentQueue<EventArg> pendingRemoteEvent = new ConcurrentQueue<EventArg>();


        AutoResetEvent eventPendingLocalEvent = new AutoResetEvent(false);
        AutoResetEvent eventPendingRemoteEvent = new AutoResetEvent(false);

        private ICfet2Logger logger;

        /// <summary>
        /// publis a event, not that the payload you are supplying will be wrapped in a status sample if it is not a sample already.
        /// </summary>
        /// <param name="source">the source resource path</param>
        /// <param name="eventType"></param>
        /// <param name="payload">the event value, will be wrapped in status sample if it not a sample</param>
        [Obsolete]
        public void Publish(string source, string eventType, object payload)
        {
            var sample = payload.ToStatus();
            var eventArg = new EventArg(source, eventType, sample);
            Publish(eventArg);
        }

        /// <summary>
        /// publis a event, not that the payload you are supplying will be wrapped in a status sample if it is not a sample already.
        /// </summary>
        /// <param name="eventArg"></param>
        [Obsolete]
        public void Publish(EventArg eventArg)
        {
            if (eventArg == null)
            {
                logger.Warn("local event args is null");
                return;
            }

            pendingLocalEvent.Enqueue(eventArg);
            newLocalPendingEvent();
        }

        /// <summary>
        /// You Shouldn't call this! This is called by EventCM recieve handler!
        /// </summary>
        /// <param name="args"></param>
        internal void ReceiveRemoteEvent(EventArg args)
        {
            if (args == null)
            {
                logger.Warn("recieve args is null");
                return;
            }

            args.Host = NormalizeHost(args.Host);
            if (string.IsNullOrEmpty(args.Host))
            {
                logger.Warn("remote event host is empty");
                return;
            }

            pendingRemoteEvent.Enqueue(args);
            newRemotePendingEvent();
        }

        /// <summary>
        /// release the blocked local event handling loop
        /// </summary>
        private void newLocalPendingEvent()
        {
            eventPendingLocalEvent.Set();
        }

        /// <summary>
        /// release the blocked remote event handling loop
        /// </summary>
        private void newRemotePendingEvent()
        {
            eventPendingRemoteEvent.Set();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="handler"></param>
        /// <returns></returns>
        [Obsolete]
        public Token Subscribe(EventFilter filter, Action<EventArg> handler)
        {
            if (!string.IsNullOrEmpty(filter.Host))
            {
                filter.Host = NormalizeHost(filter.Host);

                //try subscribe remote event
                string protocol = new Uri(filter.Host).Scheme;
                var eventModule = HubMaster.getInstance().MyCommunicationManager.GetModuleForEvent(protocol);
                if (eventModule != null)
                {
                    var remoteToken = new Token(this, protocol, filter.Host);
                    try
                    {
                        var subscriber = new Subscriber(filter, handler);
                        lock (remoteSubLock)
                        {
                            remoteSubDict.Add(remoteToken.Id, subscriber);
                            addRemoteRoutes(remoteToken.Id, subscriber);
                        }
                        eventModule.Subscribe(remoteToken, filter);
                    }
                    catch
                    {
                        logger.Error($"Subscribe {filter.Host} failed!");
                        lock (remoteSubLock)
                        {
                            if (remoteSubDict.TryGetValue(remoteToken.Id, out var subscriber))
                            {
                                removeRemoteRoutes(remoteToken.Id, subscriber);
                            }
                            remoteSubDict.Remove(remoteToken.Id);
                        }
                    }
                    return remoteToken;
                }
                //no matching protocol, print log
                logger.Error("No matching remote event hub for: " + filter.Host);
                return null;
            }
            //sub local event
            var token = new Token(this);
            var localSubscriber = new Subscriber(filter, handler);
            lock (localSubLock)
            {
                localSubDict.Add(token.Id, localSubscriber);
                addLocalRoutes(token.Id, localSubscriber);
            }
            return token;
        }

        /// <summary>
        /// unsbcribe an event
        /// </summary>
        /// <param name="token"></param>
        [Obsolete]
        public void Unsubscribe(Token token)
        {
            if (token.IsRemote)
            {
                var remoteEventCM = HubMaster.getInstance().MyCommunicationManager.GetModuleForEvent(token.Protocol);
                bool removed;
                lock (remoteSubLock)
                {
                    if (remoteSubDict.TryGetValue(token.Id, out var subscriber))
                    {
                        removeRemoteRoutes(token.Id, subscriber);
                    }
                    removed = remoteSubDict.Remove(token.Id);
                }
                if (!removed)
                {
                    return;
                }

                if (remoteEventCM != null)
                {
                    try
                    {
                        remoteEventCM.UnSubScribe(token);
                    }
                    catch
                    {
                        logger.Warn($"UnSubScribe {token.Host} faild!");
                    }
                    return;
                }
            }
            lock (localSubLock)
            {
                if (localSubDict.TryGetValue(token.Id, out var subscriber))
                {
                    removeLocalRoutes(token.Id, subscriber);
                }
                localSubDict.Remove(token.Id);
            }
        }

        /// <summary>
        /// kill the handling loop 
        /// </summary>
        public void Dispose()
        {
            lock (localSubLock)
            {
                localSubDict.Clear();
                localRouteTable.Clear();
                localFallbackRoutes.Clear();
            }
            lock (remoteSubLock)
            {
                remoteSubDict.Clear();
                remoteRouteTable.Clear();
                remoteFallbackRoutes.Clear();
            }
            pendingLocalEvent = new ConcurrentQueue<EventArg>();
            pendingRemoteEvent = new ConcurrentQueue<EventArg>();
            tokenSource.Cancel();
            //unblock the loop
            newLocalPendingEvent();
            newRemotePendingEvent();
        }

        /// <summary>
        /// Get the Full Host from EventCM，and it should be this: protocol://ip:port
        /// </summary>
        /// <param name="protocol"></param>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <returns></returns>
        internal string GetFullHost(string protocol, string hostname, int port)
        {
            var eventCM = HubMaster.getInstance().MyCommunicationManager.GetModuleForEvent(protocol);
            if (eventCM != null)
            {
                string fullhost = eventCM.GetFullHost(protocol, hostname, port);
                return NormalizeHost(fullhost);
            }
            return string.Empty;
        }

        public static string NormalizeHost(string host)
        {
            if (string.IsNullOrEmpty(host))
                return string.Empty;

            var normalizedHost = host.Trim();
            if (Uri.TryCreate(normalizedHost, UriKind.Absolute, out var uri) && uri.Port >= 0)
            {
                return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{uri.Port}";
            }

            return normalizedHost.ToLowerInvariant().TrimEnd('/');
        }

        private List<KeyValuePair<Guid, Subscriber>> getLocalCandidates(EventArg eventArg)
        {
            lock (localSubLock)
            {
                return getCandidateSubscribers(
                    localSubDict,
                    localRouteTable,
                    localFallbackRoutes,
                    makeLocalRouteKey(eventArg.Source, eventArg.EventType));
            }
        }

        private List<KeyValuePair<Guid, Subscriber>> getRemoteCandidates(EventArg eventArg)
        {
            lock (remoteSubLock)
            {
                return getCandidateSubscribers(
                    remoteSubDict,
                    remoteRouteTable,
                    remoteFallbackRoutes,
                    makeRemoteRouteKey(eventArg.Host, eventArg.Source, eventArg.EventType));
            }
        }

        private static List<KeyValuePair<Guid, Subscriber>> getCandidateSubscribers(
            Dictionary<Guid, Subscriber> subscribers,
            Dictionary<string, List<Guid>> routeTable,
            List<Guid> fallbackRoutes,
            string routeKey)
        {
            var ids = new HashSet<Guid>();
            if (routeTable.TryGetValue(routeKey, out var routedIds))
            {
                foreach (var id in routedIds)
                {
                    ids.Add(id);
                }
            }

            foreach (var id in fallbackRoutes)
            {
                ids.Add(id);
            }

            var result = new List<KeyValuePair<Guid, Subscriber>>();
            foreach (var id in ids)
            {
                if (subscribers.TryGetValue(id, out var subscriber))
                {
                    result.Add(new KeyValuePair<Guid, Subscriber>(id, subscriber));
                }
            }
            return result;
        }

        private void invokeLocalHandlerIfSubscribed(Guid tokenId, Subscriber subscriber, EventArg eventArg)
        {
            lock (localSubLock)
            {
                if (!localSubDict.ContainsKey(tokenId))
                {
                    return;
                }
            }

            invokeHandler(subscriber, eventArg);
        }

        private void invokeRemoteHandlerIfSubscribed(Guid tokenId, Subscriber subscriber, EventArg eventArg)
        {
            lock (remoteSubLock)
            {
                if (!remoteSubDict.ContainsKey(tokenId))
                {
                    return;
                }
            }

            invokeHandler(subscriber, eventArg);
        }

        private void invokeHandler(Subscriber subscriber, EventArg eventArg)
        {
            try
            {
                subscriber.Handler(eventArg);
            }
            catch (System.Exception ex)
            {
                logger.Error($"Event handler failed: {ex.Message}");
            }
        }

        private bool tryMatchSubscriber(Subscriber subscriber, EventArg eventArg)
        {
            try
            {
                return subscriber.Filter.Predicate(eventArg);
            }
            catch (System.Exception ex)
            {
                logger.Error($"Event filter failed: {ex.Message}");
                return false;
            }
        }

        private void addLocalRoutes(Guid tokenId, Subscriber subscriber)
        {
            addRoutes(localRouteTable, localFallbackRoutes, tokenId, subscriber, null);
        }

        private void addRemoteRoutes(Guid tokenId, Subscriber subscriber)
        {
            addRoutes(remoteRouteTable, remoteFallbackRoutes, tokenId, subscriber, subscriber.Filter.Host);
        }

        private void removeLocalRoutes(Guid tokenId, Subscriber subscriber)
        {
            removeRoutes(localRouteTable, localFallbackRoutes, tokenId, subscriber, null);
        }

        private void removeRemoteRoutes(Guid tokenId, Subscriber subscriber)
        {
            removeRoutes(remoteRouteTable, remoteFallbackRoutes, tokenId, subscriber, subscriber.Filter.Host);
        }

        private static void addRoutes(
            Dictionary<string, List<Guid>> routeTable,
            List<Guid> fallbackRoutes,
            Guid tokenId,
            Subscriber subscriber,
            string host)
        {
            if (!tryGetExactRouteKeys(subscriber.Filter, host, out var routeKeys))
            {
                fallbackRoutes.Add(tokenId);
                return;
            }

            foreach (var routeKey in routeKeys)
            {
                if (!routeTable.TryGetValue(routeKey, out var tokenIds))
                {
                    tokenIds = new List<Guid>();
                    routeTable.Add(routeKey, tokenIds);
                }
                tokenIds.Add(tokenId);
            }
        }

        private static void removeRoutes(
            Dictionary<string, List<Guid>> routeTable,
            List<Guid> fallbackRoutes,
            Guid tokenId,
            Subscriber subscriber,
            string host)
        {
            if (!tryGetExactRouteKeys(subscriber.Filter, host, out var routeKeys))
            {
                fallbackRoutes.Remove(tokenId);
                return;
            }

            foreach (var routeKey in routeKeys)
            {
                if (routeTable.TryGetValue(routeKey, out var tokenIds))
                {
                    tokenIds.Remove(tokenId);
                    if (tokenIds.Count == 0)
                    {
                        routeTable.Remove(routeKey);
                    }
                }
            }
        }

        private static bool tryGetExactRouteKeys(EventFilter filter, string host, out List<string> routeKeys)
        {
            routeKeys = new List<string>();
            foreach (var sourceAndType in filter.SourcesAndTypes)
            {
                if (hasRegexSpecialCharacters(sourceAndType.Source) ||
                    hasRegexSpecialCharacters(sourceAndType.EventType))
                {
                    routeKeys.Clear();
                    return false;
                }

                routeKeys.Add(string.IsNullOrEmpty(host)
                    ? makeLocalRouteKey(sourceAndType.Source, sourceAndType.EventType)
                    : makeRemoteRouteKey(host, sourceAndType.Source, sourceAndType.EventType));
            }
            return routeKeys.Count > 0;
        }

        private static bool hasRegexSpecialCharacters(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return value.IndexOfAny(new[] { '\\', '^', '$', '.', '|', '?', '*', '+', '(', ')', '[', ']', '{', '}' }) >= 0;
        }

        private static string makeLocalRouteKey(string source, string eventType)
        {
            return $"{source ?? string.Empty}\u001f{eventType ?? string.Empty}";
        }

        private static string makeRemoteRouteKey(string host, string source, string eventType)
        {
            return $"{NormalizeHost(host)}\u001f{source ?? string.Empty}\u001f{eventType ?? string.Empty}";
        }
    }
}

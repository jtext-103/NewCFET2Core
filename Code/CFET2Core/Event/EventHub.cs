using Jtext103.CFET2.Core.Log;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Reflection;

namespace Jtext103.CFET2.Core.Event
{
    /// <summary>
    /// let things to subscribe and publish events
    /// the eventHub shouldn't be known, actually Publish/Sub/UnSub should be call like: MyHub.Publish
    /// </summary>
    public partial class EventHub : IDisposable
    {
        /// <summary>
        /// create the event
        /// </summary>
        public EventHub()
        {
            logger = Cfet2LogManager.GetLogger("EventHub");
            InitializeObservationConfiguration();
            changePublicationConfiguration = Config.GlobalConfig.CaptureChangePublicationConfiguration();
            resourceObservationManager = new ResourceObservationManager(this);
        }

        CancellationTokenSource tokenSource = new CancellationTokenSource();
        CancellationToken cancelToken;
        private readonly object lifecycleLock = new object();
        private volatile bool disposed;
        private Task localLoopTask;
        private Task remoteLoopTask;

        /// <summary>
        /// init the event hub: start the event handling loop
        /// </summary>
        public void Init()
        {
            lock (lifecycleLock)
            {
                ThrowIfDisposed();
                if (localLoopTask != null)
                {
                    return;
                }
                cancelToken = tokenSource.Token;
                localLoopTask = Task.Factory.StartNew(eventHandleLocalLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                remoteLoopTask = Task.Factory.StartNew(eventHandleRemoteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// the ifinit loop of handling local event
        /// </summary>
        private void eventHandleLocalLoop()
        {
            while (!cancelToken.IsCancellationRequested)
            {
                //block the handling loop untill a new event has come
                eventPendingLocalEvent.WaitOne();
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }
                EventArg eventArg;
                while (!cancelToken.IsCancellationRequested && pendingLocalEvent.TryDequeue(out eventArg))
                {
                    handleLocalEvent(eventArg);
                }
            }
        }

        private void eventHandleRemoteLoop()
        {
            while (!cancelToken.IsCancellationRequested)
            {
                eventPendingRemoteEvent.WaitOne();
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }
                EventArg eventArg;
                while (!cancelToken.IsCancellationRequested && pendingRemoteEvent.TryDequeue(out eventArg))
                {
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
                    lock (lifecycleLock)
                    {
                        if (disposed) return;
                        Task.Run(() => invokeLocalHandlerIfSubscribed(candidate.Key, subscriber, eventArg));
                    }
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
                        lock (lifecycleLock)
                        {
                            if (disposed) return;
                            Task.Run(() => invokeRemoteHandlerIfSubscribed(candidate.Key, subscriber, eventArg));
                        }
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
        private readonly Dictionary<Guid, ObservationHandleGroup> observationGroups = new Dictionary<Guid, ObservationHandleGroup>();
        private long lastRegexTimeoutWarning;


        ConcurrentQueue<EventArg> pendingLocalEvent = new ConcurrentQueue<EventArg>();
        ConcurrentQueue<EventArg> pendingRemoteEvent = new ConcurrentQueue<EventArg>();


        AutoResetEvent eventPendingLocalEvent = new AutoResetEvent(false);
        AutoResetEvent eventPendingRemoteEvent = new AutoResetEvent(false);

        private ICfet2Logger logger;

        private ResourceObservationManager resourceObservationManager;

        internal TimeSpan ResourceObservationInterval
        {
            get
            {
                return resourceObservationManager.ObservationInterval;
            }
            set
            {
                resourceObservationManager.ObservationInterval = value;
            }
        }

        internal int ObservedResourceCount
        {
            get
            {
                return resourceObservationManager.ObservedResourceCount;
            }
        }

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
            TryPublish(eventArg);
        }

        internal bool TryPublish(EventArg eventArg)
        {
            var publishedAt = DateTimeOffset.UtcNow;
            if (eventArg == null)
            {
                logger.Warn("local event args is null");
                return false;
            }

            lock (lifecycleLock)
            {
                if (disposed) return false;
                // Freeze event metadata and the context, without stamping the caller's sample.
                // Reusing an unstamped sample for another publication must get a fresh time.
                ISample sample = null;
                if (eventArg.Sample != null)
                {
                    sample = new SampleBase<object>(eventArg.Sample.Context);
                    // Preserve the source sample's evaluated value, including generic conversion.
                    sample.Context[SampleBase<object>.KeyOfVal] = eventArg.Sample.ObjectVal;
                    sample.Timestamp = eventArg.Sample.Timestamp ?? publishedAt;
                }
                pendingLocalEvent.Enqueue(new EventArg
                {
                    Source = eventArg.Source,
                    EventType = eventArg.EventType,
                    Host = eventArg.Host,
                    PerformanceLevel = eventArg.PerformanceLevel,
                    Sample = sample,
                });
                newLocalPendingEvent();
                return true;
            }
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

            lock (lifecycleLock)
            {
                if (disposed) return;
                pendingRemoteEvent.Enqueue(args);
                newRemotePendingEvent();
            }
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
            ThrowIfDisposed();
            if (!string.IsNullOrEmpty(filter.Host))
            {
                filter.Host = NormalizeHost(filter.Host);

                //try subscribe remote event
                var requestedHost = new Uri(filter.Host);
                if (string.Equals(requestedHost.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                    filter = new EventFilter(filter) { Host = GetFullHost(requestedHost.Scheme, requestedHost.Host, requestedHost.Port) };
                string protocol = new Uri(filter.Host).Scheme;
                var eventModule = HubMaster.getInstance().MyCommunicationManager.GetModuleForEvent(protocol);
                if (eventModule != null)
                {
                    var remoteToken = new Token(this, protocol, filter.Host);
                    try
                    {
                        var subscriber = new Subscriber(filter, handler);
                        lock (lifecycleLock)
                        {
                            ThrowIfDisposed();
                            lock (remoteSubLock)
                            {
                                remoteSubDict.Add(remoteToken.Id, subscriber);
                                addRemoteRoutes(remoteToken.Id, subscriber);
                            }
                        }
                        eventModule.Subscribe(remoteToken, filter);
                        // A remote call may finish after shutdown; do not leave that late subscription alive.
                        if (disposed) eventModule.UnSubScribe(remoteToken);
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
                throw new Jtext103.CFET2.Core.Exception.ProtocolNotSuportedException("No matching remote event hub for: " + filter.Host);
            }
            //sub local event
            var token = new Token(this);
            var localSubscriber = new Subscriber(filter, handler);
            lock (lifecycleLock)
            {
                ThrowIfDisposed();
                lock (localSubLock)
                {
                    localSubDict.Add(token.Id, localSubscriber);
                    addLocalRoutes(token.Id, localSubscriber);
                }
            }
            return token;
        }

        internal void ObserveResource(Token token, ResourceBase resource, ISample initialSample)
        {
            var group = AttachObservationGroup(token);
            var result = AcquireResourceObservation(resource, initialSample);
            if (result.ReasonCode == ResourceObservationReason.DeniedByAutoDenyList)
            {
                logger.Warn($"Observation Skipped for {resource.Path}: {result.Reason}");
                group.ThrowIfDisposed();
                return;
            }
            if (result.Handle == null)
                throw new Exception.BadResourceRequestException(result.Reason);
            group.Add(result.Handle);
            group.ThrowIfDisposed();
        }

        internal ObservationHandleGroup AttachObservationGroup(Token token)
        {
            lock (localSubLock)
            {
                ThrowIfDisposed();
                if (token == null || token.IsRemote || !localSubDict.ContainsKey(token.Id))
                    throw new InvalidOperationException("The local event subscription is no longer active.");
                var group = new ObservationHandleGroup();
                observationGroups.Add(token.Id, group);
                return group;
            }
        }

        internal ResourceObservationResult AcquireResourceObservation(ResourceBase resource, ISample initialSample = null)
        {
            ThrowIfDisposed();
            try
            {
                if (autoDenyFilters.Any(filter => filter.Matches(resource.Path, "changed")))
                    return new ResourceObservationResult(resource.Path, ResourceObservationStatus.Skipped,
                        "Automatic observation is denied by AutoDenyList.",
                        reasonCode: ResourceObservationReason.DeniedByAutoDenyList);
            }
            catch (RegexMatchTimeoutException ex)
            {
                return new ResourceObservationResult(resource.Path, ResourceObservationStatus.Failed,
                    "AutoDenyList matching timed out: " + ex.Message,
                    reasonCode: ResourceObservationReason.ListMatchFailed);
            }
            string reason = null;
            if (resource.ResourceType != ResourceTypes.Status && resource.ResourceType != ResourceTypes.Config)
                reason = "Only Status and Config resources support automatic observation.";
            else if (resource.SupportsNativeEvent)
                reason = "Native events do not require automatic observation.";
            else if (resource.Info.HasGetterParameters)
                reason = "Parameterized getters require active publication and do not support automatic observation.";
            else
            {
                try { ResourceObservationTypePolicy.EnsureSupported(resource); }
                catch (Exception.WrongResourceActionException ex) { reason = ex.Message; }
            }
            if (reason != null)
                return new ResourceObservationResult(resource.Path, ResourceObservationStatus.Skipped, reason);
            return resourceObservationManager.Acquire(resource, initialSample);
        }

        internal void StopResourceObservations()
        {
            // Revoke all polling first, including an initialization currently inside a getter.
            resourceObservationManager.Dispose();
            autoObservationHandles.Dispose();
        }

        internal bool IsSubscribed(Token token)
        {
            if (token == null || disposed)
            {
                return false;
            }

            if (token.IsRemote)
            {
                lock (remoteSubLock)
                {
                    return remoteSubDict.ContainsKey(token.Id);
                }
            }

            lock (localSubLock)
            {
                return localSubDict.ContainsKey(token.Id);
            }
        }

        /// <summary>
        /// unsbcribe an event
        /// </summary>
        /// <param name="token"></param>
        [Obsolete]
        public void Unsubscribe(Token token)
        {
            if (token == null || disposed)
            {
                return;
            }

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
            ObservationHandleGroup observations;
            lock (localSubLock)
            {
                if (localSubDict.TryGetValue(token.Id, out var subscriber))
                {
                    removeLocalRoutes(token.Id, subscriber);
                }
                localSubDict.Remove(token.Id);
                observationGroups.TryGetValue(token.Id, out observations);
                observationGroups.Remove(token.Id);
            }
            observations?.Dispose();
        }

        /// <summary>
        /// kill the handling loop 
        /// </summary>
        public void Dispose()
        {
            Task loops;
            bool calledFromLoop;
            lock (lifecycleLock)
            {
                if (disposed) return;
                disposed = true;
                publicationSlots.Clear();
                tokenSource.Cancel();
                newLocalPendingEvent();
                newRemotePendingEvent();
                loops = Task.WhenAll(localLoopTask ?? Task.CompletedTask, remoteLoopTask ?? Task.CompletedTask);
                calledFromLoop = Task.CurrentId.HasValue &&
                    (Task.CurrentId == localLoopTask?.Id || Task.CurrentId == remoteLoopTask?.Id);
            }

            StopResourceObservations();
            ObservationHandleGroup[] groups;
            lock (localSubLock)
            {
                groups = observationGroups.Values.ToArray();
                observationGroups.Clear();
                localSubDict.Clear();
                localRouteTable.Clear();
                localFallbackRoutes.Clear();
            }
            foreach (var group in groups) group.Dispose();
            lock (remoteSubLock)
            {
                remoteSubDict.Clear();
                remoteRouteTable.Clear();
                remoteFallbackRoutes.Clear();
            }
            // Never wait under a subscription/lifecycle lock or wait for user handlers.
            // A pathological filter/logger must not block shutdown indefinitely.
            if (!calledFromLoop)
            {
                try
                {
                    if (loops.Wait(TimeSpan.FromSeconds(5)))
                    {
                        ReleaseLoopResources();
                        return;
                    }
                }
                catch (AggregateException ex)
                {
                    ReleaseLoopResources();
                    logger.Error($"Event loops stopped with an error: {ex.Flatten().Message}");
                    return;
                }
            }

            loops.ContinueWith(completed =>
            {
                var error = completed.Exception;
                ReleaseLoopResources();
                if (error != null) logger.Error($"Event loops stopped with an error: {error.Flatten().Message}");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (!calledFromLoop) logger.Warn("Event loops did not stop within 5 seconds; resources will be released when they exit.");
        }

        private void ReleaseLoopResources()
        {
            while (pendingLocalEvent.TryDequeue(out _)) { }
            while (pendingRemoteEvent.TryDequeue(out _)) { }
            eventPendingLocalEvent.Dispose();
            eventPendingRemoteEvent.Dispose();
            tokenSource.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(EventHub));
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
                if (!string.IsNullOrWhiteSpace(fullhost)) return NormalizeHost(fullhost);
            }
            throw new Jtext103.CFET2.Core.Exception.ProtocolNotSuportedException("No available event module for " + protocol);
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
            if (disposed) return;
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
            if (disposed) return;
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
            catch (RegexMatchTimeoutException)
            {
                var now = DateTime.UtcNow.Ticks;
                var last = Interlocked.Read(ref lastRegexTimeoutWarning);
                if (now - last >= TimeSpan.TicksPerSecond &&
                    Interlocked.CompareExchange(ref lastRegexTimeoutWarning, now, last) == last)
                    logger.Warn("Event filter exceeded the 100ms regex match timeout; skipping this event match.");
                return false;
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

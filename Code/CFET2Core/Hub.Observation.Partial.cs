using System;
using System.Collections.Generic;
using System.Linq;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Resource;

namespace Jtext103.CFET2.Core
{
    public partial class Hub
    {
        /// <summary>Lists captured when this application's Core was created; later configuration requires restart.</summary>
        public Config.AutomaticObservationConfiguration ActiveObservationConfiguration => myMaster.MyEventHub.ActiveObservationConfiguration;

        /// <summary>Resident acquisition results after StartThings. Handles remain owned by the application.
        /// These are establishment-time diagnostics, not current health or live observation status.</summary>
        public IReadOnlyList<ResourceObservationResult> AutoObservationResults => myMaster.MyEventHub.AutoObservationResults;

        /// <summary>Matches the current local directory against (path, changed), without reading resources.
        /// Registration must be complete before calling; the fixed directory is not rescanned later.</summary>
        public IReadOnlyList<ResourceBase> MatchLocalResources(EventFilter filter)
        {
            var snapshot = SnapshotObservationFilter(filter);
            if (!string.IsNullOrEmpty(snapshot.Host))
                throw new ArgumentException("Resource discovery only accepts local filters.", nameof(filter));
            return MatchLocalResourcesSnapshot(snapshot);
        }

        private IReadOnlyList<ResourceBase> MatchLocalResourcesSnapshot(EventFilter snapshot)
        {
            return Array.AsReadOnly(myMaster.Resources.Values.ToArray()
                .Where(resource => snapshot.Matches(resource.Path, "changed"))
                .GroupBy(resource => resource.Path, StringComparer.InvariantCultureIgnoreCase)
                .Select(group => group.First()).ToArray());
        }

        /// <summary>Acquires one independently disposable observation reference. No event subscription is created.</summary>
        public ResourceObservationResult AcquireResourceObservation(ResourceBase resource)
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            var master = myMaster;
            if (resource.Path == null || !master.Resources.TryGetValue(resource.Path, out var registered) ||
                !ReferenceEquals(registered, resource))
                throw new ArgumentException("Only a currently registered local resource can be observed.", nameof(resource));
            return master.MyEventHub.AcquireResourceObservation(resource);
        }

        /// <summary>Subscribes to the original event filter and observes matching eligible local resources.
        /// Initial read failures are reported individually and never retried automatically.
        /// Remote filters use the existing event transport; per-resource results are unavailable remotely.</summary>
        public ResourceGroupSubscription SubscribeResources(EventFilter filter, Action<EventArg> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var snapshot = SnapshotObservationFilter(filter);
            var eventHub = myMaster.MyEventHub;
            bool remote = !string.IsNullOrEmpty(snapshot.Host);
            // Complete matching before registering anything, so a timeout cannot leave a partial subscription.
            var resources = remote ? Array.Empty<ResourceBase>() : MatchLocalResourcesSnapshot(snapshot);
            var token = eventHub.Subscribe(snapshot, handler);
            try
            {
                if (!eventHub.IsSubscribed(token))
                    throw new BadResourceRequestException("Resource group event subscription failed.");
                var results = new List<ResourceObservationResult>();
                if (!remote)
                {
                    var group = eventHub.AttachObservationGroup(token);
                    foreach (var resource in resources)
                    {
                        group.ThrowIfDisposed();
                        var result = eventHub.AcquireResourceObservation(resource);
                        if (result.Handle != null) group.Add(result.Handle);
                        results.Add(result);
                        if (result.Status == ResourceObservationStatus.Failed || result.Status == ResourceObservationStatus.Skipped)
                            logger.Warn($"Observation {result.Status} for {result.ResourcePath}: {result.Reason}");
                    }
                    group.ThrowIfDisposed();
                }
                return new ResourceGroupSubscription(token, results);
            }
            catch
            {
                token?.Dispose();
                throw;
            }
        }

        private static EventFilter SnapshotObservationFilter(EventFilter filter)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            if (filter.SourcesAndTypes.Count == 0)
                throw new ArgumentException("At least one source/type condition is required.", nameof(filter));
            return new EventFilter(filter);
        }
    }
}

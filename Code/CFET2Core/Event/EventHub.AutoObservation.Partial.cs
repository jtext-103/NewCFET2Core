using System;
using System.Collections.Generic;
using System.Linq;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Resource;

namespace Jtext103.CFET2.Core.Event
{
    public partial class EventHub
    {
        private readonly object autoObservationLock = new object();
        private readonly ObservationHandleGroup autoObservationHandles = new ObservationHandleGroup();
        private bool autoObservationInitializationStarted;
        private IReadOnlyList<ResourceObservationResult> autoObservationResults = Array.AsReadOnly(new ResourceObservationResult[0]);
        private EventFilter[] autoObserveFilters;
        private EventFilter[] autoDenyFilters;

        internal AutomaticObservationConfiguration ActiveObservationConfiguration { get; private set; }
        internal IReadOnlyList<ResourceObservationResult> AutoObservationResults
        {
            get { lock (autoObservationLock) return autoObservationResults; }
        }

        private void InitializeObservationConfiguration()
        {
            ActiveObservationConfiguration = GlobalConfig.CaptureObservationConfiguration();
            autoObserveFilters = ActiveObservationConfiguration.AutoObserveList.Select(rule => rule.Compile()).ToArray();
            autoDenyFilters = ActiveObservationConfiguration.AutoDenyList.Select(rule => rule.Compile()).ToArray();
        }

        // Called once after all Thing.Start calls succeed. No subscription or event filter is created.
        internal void InitializeAutoObservations(IEnumerable<ResourceBase> directory)
        {
            lock (autoObservationLock)
            {
                ThrowIfDisposed();
                autoObservationHandles.ThrowIfDisposed();
                if (autoObservationInitializationStarted) return;
                autoObservationInitializationStarted = true;
            }

            var results = new List<ResourceObservationResult>();
            try
            {
                var resources = directory.ToArray();
                var selected = new Dictionary<string, ResourceBase>(StringComparer.InvariantCultureIgnoreCase);
                // Finish all positive matching before any getter runs. A timeout cannot create a partial batch.
                for (int i = 0; i < autoObserveFilters.Length; i++)
                {
                    var matched = resources.Where(resource => autoObserveFilters[i].Matches(resource.Path, "changed")).ToArray();
                    if (matched.Length == 0)
                        logger.Warn("AutoObserveList matched no registered resource: " + ActiveObservationConfiguration.AutoObserveList[i]);
                    foreach (var resource in matched) selected[resource.Path] = resource;
                }
                for (int i = 0; i < autoDenyFilters.Length; i++)
                {
                    if (!resources.Any(resource => autoDenyFilters[i].Matches(resource.Path, "changed")))
                        logger.Warn("AutoDenyList matched no registered resource: " + ActiveObservationConfiguration.AutoDenyList[i]);
                }

                foreach (var resource in selected.Values.OrderBy(resource => resource.Path, StringComparer.InvariantCultureIgnoreCase))
                {
                    autoObservationHandles.ThrowIfDisposed();
                    var result = AcquireResourceObservation(resource);
                    if (result.Handle != null) autoObservationHandles.Add(result.Handle);
                    // The application owns resident handles. Diagnostics must not let callers release them.
                    results.Add(new ResourceObservationResult(result.ResourcePath, result.Status, result.Reason,
                        reasonCode: result.ReasonCode));
                    if (result.Status == ResourceObservationStatus.Skipped || result.Status == ResourceObservationStatus.Failed)
                        logger.Warn($"AutoObserveList {result.Status} for {result.ResourcePath}: {result.Reason}");
                }
                autoObservationHandles.ThrowIfDisposed();
            }
            catch
            {
                autoObservationHandles.Dispose();
                throw;
            }
            finally
            {
                lock (autoObservationLock) autoObservationResults = Array.AsReadOnly(results.ToArray());
            }
        }
    }
}

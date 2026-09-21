using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Log;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;

namespace Jtext103.CFET2.Core.Event
{
    internal class ResourceObservationManager : IDisposable
    {
        private class ResourceObservation
        {
            public ResourceObservation(ResourceBase resource, ISample initialSample, CancellationTokenSource cancellation)
            {
                Resource = resource;
                LastValue = initialSample.ObjectVal;
                Cancellation = cancellation;
            }

            public ResourceBase Resource { get; }

            public object LastValue { get; set; }

            public int RefCount { get; set; }

            public CancellationTokenSource Cancellation { get; }

            public Task ObserveTask { get; set; }
        }

        private readonly EventHub eventHub;
        private readonly ICfet2Logger logger = Cfet2LogManager.GetLogger("ResourceObservationManager");
        private readonly object observationLock = new object();
        private readonly Dictionary<string, ResourceObservation> observations = new Dictionary<string, ResourceObservation>(StringComparer.InvariantCultureIgnoreCase);
        private readonly Dictionary<Guid, string> tokenToResourcePath = new Dictionary<Guid, string>();
        private bool disposed;

        public ResourceObservationManager(EventHub eventHub)
        {
            this.eventHub = eventHub;
        }

        public TimeSpan ObservationInterval { get; set; } = TimeSpan.FromMilliseconds(GlobalConfig.ResourceObservationIntervalMs);

        internal int ObservedResourceCount
        {
            get
            {
                lock (observationLock)
                {
                    return observations.Count;
                }
            }
        }

        public void Observe(Token token, ResourceBase resource, ISample initialSample)
        {
            ResourceObservation observation;
            var shouldStart = false;
            lock (observationLock)
            {
                if (disposed)
                {
                    return;
                }

                if (tokenToResourcePath.ContainsKey(token.Id))
                {
                    return;
                }

                if (!observations.TryGetValue(resource.Path, out observation))
                {
                    observation = new ResourceObservation(resource, initialSample, new CancellationTokenSource());
                    observations.Add(resource.Path, observation);
                    shouldStart = true;
                }
                observation.RefCount++;
                tokenToResourcePath.Add(token.Id, resource.Path);
                if (shouldStart)
                {
                    observation.ObserveTask = Task.Run(() => observeLoop(observation));
                }
            }
        }

        public void Unobserve(Token token)
        {
            ResourceObservation observationToStop = null;
            lock (observationLock)
            {
                if (!tokenToResourcePath.TryGetValue(token.Id, out var resourcePath))
                {
                    return;
                }

                tokenToResourcePath.Remove(token.Id);
                if (!observations.TryGetValue(resourcePath, out var observation))
                {
                    return;
                }

                observation.RefCount--;
                if (observation.RefCount <= 0)
                {
                    observations.Remove(resourcePath);
                    observationToStop = observation;
                }
            }

            if (observationToStop != null)
            {
                observationToStop.Cancellation.Cancel();
                observationToStop.ObserveTask?.ContinueWith(_ => observationToStop.Cancellation.Dispose());
            }
        }

        public void Dispose()
        {
            List<ResourceObservation> observationsToStop;
            lock (observationLock)
            {
                disposed = true;
                observationsToStop = new List<ResourceObservation>(observations.Values);
                observations.Clear();
                tokenToResourcePath.Clear();
            }

            foreach (var observation in observationsToStop)
            {
                observation.Cancellation.Cancel();
                // The getter may still be running; release its cancellation source only after the loop exits.
                observation.ObserveTask.ContinueWith(_ => observation.Cancellation.Dispose(), TaskScheduler.Default);
            }
        }

        private async Task observeLoop(ResourceObservation observation)
        {
            while (!observation.Cancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ObservationInterval, observation.Cancellation.Token);
                    if (observation.Cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    var current = observation.Resource.Get() as ISample;
                    if (current == null || !current.IsValid)
                    {
                        continue;
                    }

                    var currentValue = current.ObjectVal;
                    var changed = false;
                    lock (observationLock)
                    {
                        if (!observations.ContainsKey(observation.Resource.Path))
                        {
                            return;
                        }

                        changed = !object.Equals(observation.LastValue, currentValue);
                        if (changed)
                        {
                            observation.LastValue = currentValue;
                        }
                    }

                    if (changed)
                    {
                        eventHub.Publish(observation.Resource.Path, "changed", currentValue);
                    }
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (System.Exception ex)
                {
                    logger.Warn($"Observe resource {observation.Resource.Path} failed: {ex.Message}");
                }
            }
        }

    }
}

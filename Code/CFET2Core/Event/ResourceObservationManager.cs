using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Log;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;

namespace Jtext103.CFET2.Core.Event
{
    internal sealed class ResourceObservationManager : IDisposable
    {
        // The fixed directory bounds slot lifetime. A stable read gate serializes an old,
        // uncancellable getter with initialization after the last reference was released.
        private sealed class ResourceSlot
        {
            public readonly object ReadGate = new object();
            public ResourceObservation Current;
        }

        private sealed class ResourceObservation
        {
            public ResourceBase Resource;
            public object LastValue;
            public readonly Dictionary<Guid, ObservationHandle> Handles = new Dictionary<Guid, ObservationHandle>();
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public Task ObserveTask { get; set; }
        }

        private readonly EventHub eventHub;
        private readonly ICfet2Logger logger = Cfet2LogManager.GetLogger("ResourceObservationManager");
        private readonly object sync = new object();
        private readonly Dictionary<string, ResourceSlot> slots = new Dictionary<string, ResourceSlot>(StringComparer.InvariantCultureIgnoreCase);
        private bool disposed;

        public ResourceObservationManager(EventHub eventHub) { this.eventHub = eventHub; }
        public TimeSpan ObservationInterval { get; set; } = TimeSpan.FromMilliseconds(GlobalConfig.ResourceObservationIntervalMs);

        internal int ObservedResourceCount
        {
            get { lock (sync) return slots.Values.Count(slot => slot.Current != null); }
        }

        public ResourceObservationResult Acquire(ResourceBase resource, ISample initialSample = null)
        {
            ResourceSlot slot;
            lock (sync)
            {
                ThrowIfDisposed();
                if (!slots.TryGetValue(resource.Path, out slot))
                    slots.Add(resource.Path, slot = new ResourceSlot());
                if (slot.Current != null) return AddHandle(slot, slot.Current, ResourceObservationStatus.Shared);
            }

            lock (slot.ReadGate)
            {
                lock (sync)
                {
                    ThrowIfDisposed();
                    if (slot.Current != null) return AddHandle(slot, slot.Current, ResourceObservationStatus.Shared);
                }

                // Never execute device code with the manager/state lock held.
                object initialValue = null;
                string failure = null;
                try
                {
                    var sample = initialSample ?? SlowResourceReadMonitor.Measure(resource.Path, "initial", () => resource.Get() as ISample);
                    if (sample == null || !sample.IsValid)
                        failure = "Initial read returned an invalid sample.";
                    else
                        initialValue = sample.ObjectVal;
                }
                catch (System.Exception ex) { failure = "Initial read failed: " + ex.Message; }

                lock (sync)
                {
                    ThrowIfDisposed();
                    if (failure != null)
                        return new ResourceObservationResult(resource.Path, ResourceObservationStatus.Failed, failure);
                    var observation = new ResourceObservation { Resource = resource, LastValue = initialValue };
                    slot.Current = observation;
                    var result = AddHandle(slot, observation, ResourceObservationStatus.Started);
                    observation.ObserveTask = Task.Run(() => ObserveLoop(slot, observation));
                    return result;
                }
            }
        }

        private ResourceObservationResult AddHandle(ResourceSlot slot, ResourceObservation observation, ResourceObservationStatus status)
        {
            ObservationHandle handle = null;
            handle = new ObservationHandle(observation.Resource.Path, () => Release(slot, observation, handle.Id));
            observation.Handles.Add(handle.Id, handle);
            return new ResourceObservationResult(observation.Resource.Path, status, handle: handle);
        }

        private void Release(ResourceSlot slot, ResourceObservation observation, Guid id)
        {
            lock (sync)
            {
                if (!observation.Handles.Remove(id) || observation.Handles.Count != 0) return;
                if (ReferenceEquals(slot.Current, observation)) slot.Current = null;
                observation.Cancellation.Cancel();
            }
        }

        public void Dispose()
        {
            ObservationHandle[] handles;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                handles = slots.Values.Where(s => s.Current != null)
                    .SelectMany(s => s.Current.Handles.Values).ToArray();
                foreach (var slot in slots.Values)
                {
                    if (slot.Current == null) continue;
                    slot.Current.Handles.Clear();
                    slot.Current.Cancellation.Cancel();
                    slot.Current = null;
                }
                slots.Clear();
            }
            foreach (var handle in handles) handle.Dispose();
        }

        private bool IsCurrent(ResourceSlot slot, ResourceObservation observation)
        {
            return !disposed && ReferenceEquals(slot.Current, observation) && observation.Handles.Count != 0;
        }

        private async Task ObserveLoop(ResourceSlot slot, ResourceObservation observation)
        {
            try
            {
                while (!observation.Cancellation.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(ObservationInterval, observation.Cancellation.Token).ConfigureAwait(false);
                        lock (slot.ReadGate)
                        {
                            lock (sync) { if (!IsCurrent(slot, observation)) return; }
                            var current = SlowResourceReadMonitor.Measure(observation.Resource.Path, "poll", () => observation.Resource.Get() as ISample);
                            var observedAt = DateTimeOffset.UtcNow;
                            if (current == null || !current.IsValid) continue;
                            var value = current.ObjectVal;
                            var sample = new Status<object>(current.Context);
                            sample.Context[SampleBase<object>.KeyOfVal] = value;
                            sample.Timestamp = current.Timestamp ?? observedAt;
                            lock (sync)
                            {
                                if (!IsCurrent(slot, observation)) return;
                                if (object.Equals(observation.LastValue, value)) continue;
                                observation.LastValue = value;
                                // Publish enqueues only; user handlers execute outside this lock.
                                eventHub.Publish(observation.Resource.Path, "changed", sample);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (observation.Cancellation.IsCancellationRequested) { return; }
                    catch (System.Exception ex)
                    {
                        logger.Warn($"Observe resource {observation.Resource.Path} failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                lock (sync) observation.Cancellation.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ResourceObservationManager));
        }
    }
}

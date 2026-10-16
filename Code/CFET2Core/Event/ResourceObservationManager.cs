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
            public readonly object StateGate = new object();
            public ResourceObservation Current;
        }

        private sealed class ResourceObservation
        {
            public ResourceBase Resource;
            public ResourceValueSnapshot LastValue;
            public readonly Dictionary<Guid, ObservationHandle> Handles = new Dictionary<Guid, ObservationHandle>();
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public Task ObserveTask { get; set; }
            public bool LoopCompleted;
        }

        private readonly EventHub eventHub;
        private readonly ICfet2Logger logger = Cfet2LogManager.GetLogger("ResourceObservationManager");
        private readonly object sync = new object();
        private readonly Dictionary<string, ResourceSlot> slots = new Dictionary<string, ResourceSlot>(StringComparer.InvariantCultureIgnoreCase);
        private volatile bool disposed;
        private int observedResourceCount;

        public ResourceObservationManager(EventHub eventHub) { this.eventHub = eventHub; }
        public TimeSpan ObservationInterval { get; set; } = TimeSpan.FromMilliseconds(GlobalConfig.ResourceObservationIntervalMs);

        internal int ObservedResourceCount
        {
            get { return Volatile.Read(ref observedResourceCount); }
        }

        public ResourceObservationResult Acquire(ResourceBase resource, ISample initialSample = null)
        {
            ResourceSlot slot;
            lock (sync)
            {
                ThrowIfDisposed();
                if (!slots.TryGetValue(resource.Path, out slot))
                    slots.Add(resource.Path, slot = new ResourceSlot());
            }
            // Never hold the directory lock while waiting for a resource or EventHub.
            lock (slot.StateGate)
            {
                ThrowIfDisposed();
                if (slot.Current != null) return AddHandle(slot, slot.Current, ResourceObservationStatus.Shared);
            }

            lock (slot.ReadGate)
            {
                lock (slot.StateGate)
                {
                    ThrowIfDisposed();
                    if (slot.Current != null) return AddHandle(slot, slot.Current, ResourceObservationStatus.Shared);
                }

                // Never execute device code with the manager/state lock held.
                ResourceValueSnapshot initialValue = null;
                string failure = null;
                try
                {
                    var sample = initialSample ?? SlowResourceReadMonitor.Measure(resource.Path, "initial", () => resource.Get() as ISample);
                    if (sample == null || !sample.IsValid)
                        failure = "Initial read returned an invalid sample.";
                    else
                        initialValue = ResourceValueComparison.Capture(ResourceObservationTypePolicy.GetValueType(resource), sample.ObjectVal);
                }
                catch (System.Exception ex) { failure = "Initial read failed: " + ex.Message; }

                lock (slot.StateGate)
                {
                    ThrowIfDisposed();
                    if (failure != null)
                        return new ResourceObservationResult(resource.Path, ResourceObservationStatus.Failed, failure);
                    var observation = new ResourceObservation { Resource = resource, LastValue = initialValue };
                    slot.Current = observation;
                    Interlocked.Increment(ref observedResourceCount);
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
            lock (slot.StateGate)
            {
                if (!observation.Handles.Remove(id) || observation.Handles.Count != 0) return;
                if (ReferenceEquals(slot.Current, observation))
                {
                    slot.Current = null;
                    Interlocked.Decrement(ref observedResourceCount);
                }
                CancelObservation(observation);
            }
        }

        public void Dispose()
        {
            ResourceSlot[] snapshot;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                snapshot = slots.Values.ToArray();
                slots.Clear();
            }
            foreach (var slot in snapshot)
            {
                ObservationHandle[] handles;
                lock (slot.StateGate)
                {
                    if (slot.Current == null) continue;
                    handles = slot.Current.Handles.Values.ToArray();
                    slot.Current.Handles.Clear();
                    CancelObservation(slot.Current);
                    slot.Current = null;
                    Interlocked.Decrement(ref observedResourceCount);
                }
                foreach (var handle in handles) handle.Dispose();
            }
        }

        private bool IsCurrent(ResourceSlot slot, ResourceObservation observation)
        {
            return !disposed && ReferenceEquals(slot.Current, observation) && observation.Handles.Count != 0;
        }

        // Called with StateGate held. A loop can finish after manager shutdown is marked,
        // before Dispose reaches this slot; keep its CTS alive until its handles are revoked.
        private static void CancelObservation(ResourceObservation observation)
        {
            if (observation.LoopCompleted) observation.Cancellation.Dispose();
            else observation.Cancellation.Cancel();
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
                            lock (slot.StateGate) { if (!IsCurrent(slot, observation)) return; }
                            var current = SlowResourceReadMonitor.Measure(observation.Resource.Path, "poll", () => observation.Resource.Get() as ISample);
                            var observedAt = DateTimeOffset.UtcNow;
                            if (current == null || !current.IsValid) continue;
                            var value = ResourceValueComparison.Capture(ResourceObservationTypePolicy.GetValueType(observation.Resource), current.ObjectVal);
                            if (!ResourceValueComparison.HasChanged(observation.LastValue, value, eventHub.ResolveMinDelta(observation.Resource))) continue;
                            var sample = new Status<object>(current.Context);
                            sample.Context[SampleBase<object>.KeyOfVal] = value.CreateEventValue();
                            sample.Timestamp = current.Timestamp ?? observedAt;
                            var prepared = new EventArg(observation.Resource.Path, "changed", sample);
                            lock (slot.StateGate)
                            {
                                if (!IsCurrent(slot, observation)) return;
                                // Serialize revocation with admission for this resource only.
                                if (eventHub.TryEnqueuePrepared(prepared))
                                    observation.LastValue = value;
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
                lock (slot.StateGate)
                {
                    observation.LoopCompleted = true;
                    if (observation.Handles.Count == 0) observation.Cancellation.Dispose();
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ResourceObservationManager));
        }
    }
}

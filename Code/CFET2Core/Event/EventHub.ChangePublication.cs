using System;
using System.Collections.Generic;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;

namespace Jtext103.CFET2.Core.Event
{
    public partial class EventHub
    {
        internal void EnsurePublicationOpen() { lock (lifecycleLock) ThrowIfDisposed(); }
        private sealed class PublicationSlot
        {
            internal readonly object Gate = new object();
            internal ResourceValueSnapshot Baseline;
        }
        private readonly Dictionary<ResourceBase, Dictionary<string, PublicationSlot>> publicationSlots =
            new Dictionary<ResourceBase, Dictionary<string, PublicationSlot>>();

        internal bool PublishIfChanged<T>(ResourceBase resource, string path, T value, DateTimeOffset? timestamp)
        {
            PublicationSlot slot;
            lock (lifecycleLock)
            {
                ThrowIfDisposed();
                if (!publicationSlots.TryGetValue(resource, out var paths))
                    publicationSlots.Add(resource, paths = new Dictionary<string, PublicationSlot>(StringComparer.Ordinal));
                if (!paths.TryGetValue(path, out slot)) paths.Add(path, slot = new PublicationSlot());
            }
            lock (slot.Gate)
            {
                lock (lifecycleLock) ThrowIfDisposed();
                ResourceValueSnapshot current;
                try { current = ResourceValueComparison.Capture(value); }
                catch (NotSupportedException ex) { throw new NotSupportedException($"Resource '{path}': {ex.Message}", ex); }
                if (slot.Baseline != null && !ResourceValueComparison.HasChanged(slot.Baseline, current))
                {
                    EnsurePublicationOpen();
                    return false;
                }
                var sample = new Status<object>(current.CreateEventValue()) { Timestamp = timestamp };
                if (!TryPublish(new EventArg(path, "changed", sample))) throw new ObjectDisposedException(nameof(EventHub));
                slot.Baseline = current;
                return true;
            }
        }
    }
}

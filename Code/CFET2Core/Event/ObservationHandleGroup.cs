using System;
using System.Collections.Generic;

namespace Jtext103.CFET2.Core.Event
{
    // Attached to the event token before any getter runs, so late acquisitions cannot outlive unsubscribe.
    internal sealed class ObservationHandleGroup : IDisposable
    {
        private readonly object sync = new object();
        private readonly List<ObservationHandle> handles = new List<ObservationHandle>();
        private bool disposed;

        public void ThrowIfDisposed()
        {
            lock (sync)
                if (disposed) throw new ObjectDisposedException(nameof(ObservationHandleGroup));
        }

        public void Add(ObservationHandle handle)
        {
            lock (sync)
            {
                if (!disposed) { handles.Add(handle); return; }
            }
            handle.Dispose();
            throw new ObjectDisposedException(nameof(ObservationHandleGroup));
        }

        public void Dispose()
        {
            ObservationHandle[] snapshot;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                snapshot = handles.ToArray();
                handles.Clear();
            }
            List<System.Exception> failures = null;
            foreach (var handle in snapshot)
            {
                try { handle.Dispose(); }
                catch (System.Exception ex) { (failures ?? (failures = new List<System.Exception>())).Add(ex); }
            }
            if (failures != null) throw new AggregateException(failures);
        }
    }
}

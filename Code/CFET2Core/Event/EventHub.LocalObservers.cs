using System;
using System.Collections.Generic;

namespace Jtext103.CFET2.Core.Event
{
    public partial class EventHub
    {
        private Action<EventArg>[] localEventObservers = Array.Empty<Action<EventArg>>();
        private readonly List<Action<EventArg>> pendingLocalEventObservers = new List<Action<EventArg>>();
        private volatile bool localEventObserversStarted;
        private volatile bool localEventObserversClosed;

        internal bool IsDisposed => disposed;
        internal object ObserverRegistrationLock => lifecycleLock;

        // The owning HubMaster holds its startup registration lock across this call.
        internal void RegisterLocalEventObserver(Action<EventArg> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (lifecycleLock)
            {
                ThrowIfDisposed();
                if (localEventObserversClosed) throw new ObjectDisposedException(nameof(EventHub));
                if (localEventObserversStarted) throw new InvalidOperationException("Local observers must be registered before StartThings.");
                pendingLocalEventObservers.Add(callback);
            }
        }

        // Called under the shared startup/lifecycle lock before the first Thing.Start.
        internal void StartLocalEventObservers()
        {
            ThrowIfDisposed();
            localEventObservers = pendingLocalEventObservers.ToArray();
            pendingLocalEventObservers.Clear();
            // Release/acquire publishes the fixed array to the already-running event loop.
            localEventObserversStarted = true;
        }

        private void NotifyLocalEventObservers(EventArg eventArg)
        {
            if (!localEventObserversStarted || localEventObserversClosed || disposed) return;
            var observers = localEventObservers;
            foreach (var callback in observers)
            {
                if (disposed || localEventObserversClosed) return;
                try { callback.Invoke(eventArg); }
                catch (System.Exception ex)
                {
                    logger.Error($"Local event observer failed ({ex.GetType().Name}); continuing dispatch.");
                }
            }
        }

        internal void CloseLocalEventObservers()
        {
            lock (lifecycleLock)
            {
                localEventObserversClosed = true;
                pendingLocalEventObservers.Clear();
                localEventObservers = Array.Empty<Action<EventArg>>();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Log;

namespace Jtext103.CFET2.Core
{
    public partial class Hub
    {
        /// <summary>True after Thing startup and automatic observation initialization succeed.
        /// Bound to this Hub's original Core, including after Core replacement.</summary>
        public bool IsLocalResourcesReady => publicationMaster.IsLocalResourcesReady;

        /// <summary>Registers a synchronous, one-shot startup notification before StartThings begins.
        /// Late registration throws instead of replaying the notification. Core owns cleanup.</summary>
        public void RegisterLocalResourcesReadyObserver(Action callback) =>
            publicationMaster.RegisterLocalResourcesReadyObserver(callback);
    }

    public partial class HubMaster
    {
        // Registration, readiness, event admission and shutdown share one short state lock.
        // Sample evaluation and all application callbacks run outside it.
        private object resourceReadinessLock => MyEventHub.ObserverRegistrationLock;
        private readonly List<Action> resourceReadyObservers = new List<Action>();
        private Action[] fixedResourceReadyObservers = Array.Empty<Action>();
        private readonly ICfet2Logger readinessLogger = Cfet2LogManager.GetLogger("Core.ResourceReadiness");
        private bool localResourcesReady;
        private bool observerRegistrationClosed;
        private bool resourceStartupFailed;
        private volatile bool resourceReadinessClosed;

        internal bool IsLocalResourcesReady
        {
            get { lock (resourceReadinessLock) return localResourcesReady && !resourceReadinessClosed && !MyEventHub.IsDisposed; }
        }

        internal void RegisterLocalEventObserver(Action<EventArg> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (resourceReadinessLock)
            {
                EnsureObserverRegistrationOpen();
                MyEventHub.RegisterLocalEventObserver(callback);
            }
        }

        internal void RegisterLocalResourcesReadyObserver(Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (resourceReadinessLock)
            {
                EnsureObserverRegistrationOpen();
                resourceReadyObservers.Add(callback);
            }
        }

        private void EnsureObserverRegistrationOpen()
        {
            if (resourceReadinessClosed || MyEventHub.IsDisposed) throw new ObjectDisposedException(nameof(HubMaster));
            if (observerRegistrationClosed)
                throw new InvalidOperationException("Local observers must be registered before StartThings begins.");
        }

        internal void BeginResourceStartup()
        {
            lock (resourceReadinessLock)
            {
                EnsureObserverRegistrationOpen();
                fixedResourceReadyObservers = resourceReadyObservers.ToArray();
                MyEventHub.StartLocalEventObservers();
                resourceReadyObservers.Clear();
                observerRegistrationClosed = true;
            }
        }

        internal void FailLocalResourceStartup()
        {
            lock (resourceReadinessLock)
            {
                observerRegistrationClosed = true;
                resourceStartupFailed = true;
                localResourcesReady = false;
                resourceReadyObservers.Clear();
                fixedResourceReadyObservers = Array.Empty<Action>();
            }
        }

        internal void MarkLocalResourcesReady()
        {
            Action[] observers;
            lock (resourceReadinessLock)
            {
                if (resourceReadinessClosed || MyEventHub.IsDisposed || !observerRegistrationClosed || resourceStartupFailed || localResourcesReady) return;
                localResourcesReady = true;
                observers = fixedResourceReadyObservers;
                fixedResourceReadyObservers = Array.Empty<Action>();
            }
            foreach (var callback in observers)
            {
                if (resourceReadinessClosed || MyEventHub.IsDisposed) return;
                try { callback.Invoke(); }
                catch (System.Exception ex)
                {
                    readinessLogger.Error($"Local resources ready observer failed ({ex.GetType().Name}); continuing notification.");
                }
            }
        }

        internal void CloseLocalObservers()
        {
            lock (resourceReadinessLock)
            {
                resourceReadinessClosed = true;
                observerRegistrationClosed = true;
                resourceReadyObservers.Clear();
                fixedResourceReadyObservers = Array.Empty<Action>();
                MyEventHub.CloseLocalEventObservers();
            }
        }
    }
}

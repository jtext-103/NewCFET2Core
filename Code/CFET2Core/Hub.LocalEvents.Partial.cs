using System;
using Jtext103.CFET2.Core.Event;

namespace Jtext103.CFET2.Core
{
    public partial class Hub
    {
        /// <summary>Registers a Core-lifetime observer before StartThings begins.
        /// Callbacks run synchronously before business handlers and must only snapshot/enqueue work.
        /// Registration during/after Thing startup or shutdown throws; Core owns cleanup.</summary>
        public void RegisterLocalEventObserver(Action<EventArg> callback) =>
            publicationMaster.RegisterLocalEventObserver(callback);
    }
}

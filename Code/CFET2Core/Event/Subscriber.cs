using Jtext103.CFET2.Core.Sample;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jtext103.CFET2.Core.Event
{
    /// <summary>
    /// a subscriber created for an event
    /// </summary>
    public class Subscriber
    {
        /// <summary>
        /// when subscribeed to an event , this will be created for it
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="handler"></param>
        public Subscriber(EventFilter filter, Action<EventArg> handler) : this(filter, handler, null)
        {
        }

        internal Subscriber(EventFilter filter, Action<EventArg> handler, Communication.IRemoteEventDispatchGuard dispatchGuard)
        {
            Filter = filter;
            Handler = handler;
            DispatchGuard = dispatchGuard;
        }

        internal Communication.IRemoteEventDispatchGuard DispatchGuard { get; }


        public EventFilter Filter { get;  }

        /// <summary>
        /// the action to handle the event
        /// </summary>
        public Action<EventArg> Handler { get;  }

    }
}

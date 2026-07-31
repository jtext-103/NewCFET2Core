using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jtext103.CFET2.Core.Event
{
    /// <summary>
    /// The identify of a Subscribe
    /// </summary>
    public class Token : IDisposable
    {
        EventHub hub { get;  }
        /// <summary>
        /// 
        /// </summary>
        public  Guid Id { get;  }
        /// <summary>
        /// the remote Host，the host should be protocol::ip:port
        /// </summary>
        public string Host {  get; }
        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            hub.Unsubscribe(this);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="eventHub"></param>
        public Token(EventHub eventHub)
        {
            hub = eventHub;
            Id = Guid.NewGuid();
            Host = string.Empty;
        }

        public Token(EventHub eventHub,string protocol,string host):this(eventHub)
        {
            Protocol = protocol;
            IsRemote = true;
            Host = EventHub.NormalizeHost(host);
        }

        /// <summary>
        /// is this token point to a remote subscription
        /// </summary>
        public bool IsRemote { get; } = false;

        /// <summary>
        /// the remote subscription protocol
        /// </summary>
        public string Protocol { get; }
    }
}

using Jtext103.CFET2.Core.Event;
using System;
using System.Collections.Generic;
using System.Text;

namespace Jtext103.CFET2.Core.Communication
{
    public abstract class IEventCommunicationModule: CFET2Module,IDisposable
    {
        /// <summary>
        /// the protocal name this module can handle, like for dds is DDS for REST is HTTP
        /// </summary>
        public abstract string[] ProtocolNames { get; }

        /// <summary>
        /// this is called after all things have been started,you should make sure invoke start twice or more is ok
        /// </summary>
        public virtual void Start()
        {

        }

        public abstract void Dispose();

        /// <summary>
        /// it should be this: protocol://ip:port
        /// </summary>
        /// <param name="protocol"></param>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <returns></returns>
        public abstract string GetFullHost(string protocol, string hostname, int port);


        #region Subscribe UnSubscribe
        public abstract void Subscribe(Token token, EventFilter filter);
        public abstract void UnSubScribe(Token token);

        public abstract void RegisterEventCallback(Action<EventArg> hander);
        #endregion
    }
}

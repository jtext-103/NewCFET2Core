using Jtext103.CFET2.Core.Communication;
using Jtext103.CFET2.Core.Event;
using System;
using System.Collections.Generic;

namespace Jtext103.CFET2.Core.Test.TestDummies
{
    public class DummyEventCommunicationModule : IEventCommunicationModule
    {
        private readonly List<Token> subscribedTokens = new List<Token>();
        private bool isDisposed;

        public override string[] ProtocolNames => new[] { "test", "eventdummy" };

        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int SubscribeCount { get; private set; }
        public int UnsubscribeCount { get; private set; }
        public bool ThrowOnSubscribe { get; set; }
        public bool ThrowOnUnsubscribe { get; set; }
        public int SubscribedTokenCount => subscribedTokens.Count;
        public EventFilter? LastFilter { get; private set; }
        public Action<EventArg>? LastHandler { get; private set; }
        public Token? LastToken { get; private set; }

        public override void Start()
        {
            StartCount++;
        }

        public override void Dispose()
        {
            DisposeCount++;
            isDisposed = true;
            LastHandler = null;
        }

        public override string GetFullHost(string protocol, string hostname, int port)
        {
            foreach (var protocolName in ProtocolNames)
            {
                if (string.Equals(protocolName, protocol, StringComparison.InvariantCultureIgnoreCase))
                {
                    return $"{protocol.ToLower()}://{hostname}:{port}";
                }
            }
            return "";
        }

        public override void Subscribe(Token token, EventFilter filter)
        {
            SubscribeCount++;
            if (ThrowOnSubscribe)
            {
                throw new InvalidOperationException("Dummy remote subscribe failed.");
            }

            LastToken = token;
            LastFilter = filter;
            subscribedTokens.Add(token);
        }

        public override void UnSubScribe(Token token)
        {
            UnsubscribeCount++;
            LastToken = token;
            subscribedTokens.Remove(token);
            if (ThrowOnUnsubscribe)
            {
                throw new InvalidOperationException("Dummy remote unsubscribe failed.");
            }
        }

        public override void RegisterEventCallback(Action<EventArg> hander)
        {
            LastHandler = hander;
        }

        public void EmitRemoteEvent(EventArg eventArg)
        {
            if (isDisposed)
            {
                return;
            }

            LastHandler?.Invoke(eventArg);
        }
    }
}

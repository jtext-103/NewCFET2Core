using System;
using Jtext103.CFET2.Core.Sample;

namespace Jtext103.CFET2.Core.Event
{
    /// <summary>
    /// The result of resource-level subscription. It contains both the current snapshot and the token for future events.
    /// </summary>
    public class ResourceSubscription : IDisposable
    {
        public ResourceSubscription(Token token, ISample initialValue)
        {
            Token = token;
            InitialValue = initialValue;
        }

        public Token Token { get; }

        public ISample InitialValue { get; }

        public void Dispose()
        {
            Token.Dispose();
        }
    }
}

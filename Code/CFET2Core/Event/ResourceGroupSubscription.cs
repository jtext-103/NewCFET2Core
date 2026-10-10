using System;
using System.Collections.Generic;
using System.Linq;

namespace Jtext103.CFET2.Core.Event
{
    /// <summary>An event subscription and its resource observations. Disposing Token also releases all handles.</summary>
    public sealed class ResourceGroupSubscription : IDisposable
    {
        internal ResourceGroupSubscription(Token token, IEnumerable<ResourceObservationResult> results)
        {
            Token = token;
            ObservationResults = Array.AsReadOnly(results.ToArray());
        }

        public Token Token { get; }
        public IReadOnlyList<ResourceObservationResult> ObservationResults { get; }
        /// <summary>Remote protocols do not report per-resource observation results in this version.</summary>
        public bool HasLocalObservationResults => !Token.IsRemote;

        public void Dispose() => Token.Dispose();
    }
}

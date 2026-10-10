using System;
using System.Threading;

namespace Jtext103.CFET2.Core.Event
{
    /// <summary>One owner's reference to a shared resource observation. Dispose is idempotent.</summary>
    public sealed class ObservationHandle : IDisposable
    {
        private Action release;

        internal ObservationHandle(string resourcePath, Action release)
        {
            ResourcePath = resourcePath;
            this.release = release;
        }

        public Guid Id { get; } = Guid.NewGuid();
        public string ResourcePath { get; }
        public bool IsReleased => Volatile.Read(ref release) == null;

        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}

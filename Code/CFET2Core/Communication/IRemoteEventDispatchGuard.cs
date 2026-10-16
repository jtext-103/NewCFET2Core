using System;
using Jtext103.CFET2.Core.Event;

namespace Jtext103.CFET2.Core.Communication
{
    /// <summary>Optional local check of a transport's effective rules for a Token.
    /// Called before a remote business handler, outside Core locks; must not perform network I/O.
    /// False suppresses this delivery. Exceptions are logged and suppress this delivery as well.</summary>
    public interface IRemoteEventDispatchGuard
    {
        bool ShouldDispatch(Guid tokenId, EventArg eventArg);
    }
}

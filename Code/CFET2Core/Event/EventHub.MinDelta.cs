using System.Collections.Generic;
using System.Linq;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;

namespace Jtext103.CFET2.Core.Event
{
    public partial class EventHub
    {
        private readonly ResourceChangePublicationConfiguration changePublicationConfiguration;
        private bool thresholdDirectoryChecked;
        internal double ResolveMinDelta(ResourceBase resource) => changePublicationConfiguration.ResolveMinDelta(resource);

        internal void ValidateMinDeltaDirectory(IEnumerable<ResourceBase> resources)
        {
            lock (lifecycleLock)
            {
                ThrowIfDisposed();
                if (thresholdDirectoryChecked) return;
                thresholdDirectoryChecked = true;
                var directory = resources.ToArray();
                foreach (var path in changePublicationConfiguration.Paths)
                    if (!directory.Any(resource => System.StringComparer.InvariantCultureIgnoreCase.Equals(resource.Path, path) &&
                        (resource.ResourceType == ResourceTypes.Status || resource.ResourceType == ResourceTypes.Config)))
                        logger.Warn("ResourceMinDeltaOverrides path matched no local Status/Config base resource: " + path);
            }
        }
    }
}

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
        private readonly object thresholdValidationLock = new object();
        private bool thresholdDirectoryChecked;
        internal double ResolveMinDelta(ResourceBase resource) => changePublicationConfiguration.ResolveMinDelta(resource);

        internal void ValidateMinDeltaDirectory(IEnumerable<ResourceBase> resources)
        {
            string[] missing;
            // Concurrent callers wait for directory validation, without blocking publication.
            lock (thresholdValidationLock)
            {
                lock (lifecycleLock) ThrowIfDisposed();
                if (thresholdDirectoryChecked) return;
                var paths = new HashSet<string>(resources
                    .Where(resource => resource.ResourceType == ResourceTypes.Status || resource.ResourceType == ResourceTypes.Config)
                    .Select(resource => resource.Path), System.StringComparer.InvariantCultureIgnoreCase);
                missing = changePublicationConfiguration.Paths.Where(path => !paths.Contains(path)).ToArray();
                lock (lifecycleLock) ThrowIfDisposed();
                thresholdDirectoryChecked = true;
            }
            foreach (var path in missing)
                logger.Warn("ResourceMinDeltaOverrides path matched no local Status/Config base resource: " + path);
        }
    }
}

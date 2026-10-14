using Jtext103.CFET2.Core.Communication;
using Jtext103.CFET2.Core.Log;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jtext103.CFET2.Core
{
    /// <summary>
    /// manages all the remote protocols
    /// </summary>
    internal class CommunicationManager : IDisposable
    {

        Dictionary<string, CommunicationModule> moduleDict = new Dictionary<string, CommunicationModule>(StringComparer.InvariantCultureIgnoreCase);
        Dictionary<string, IEventCommunicationModule> eventModuleDict = new Dictionary<string, IEventCommunicationModule>(StringComparer.InvariantCultureIgnoreCase);

        internal Config.EventCommunicationConfiguration ActiveEventCommunicationConfiguration { get; } = Config.GlobalConfig.CaptureEventCommunicationConfiguration();
        private readonly Dictionary<Config.EventTransport, IEventCommunicationModule> transportModules = new Dictionary<Config.EventTransport, IEventCommunicationModule>();
        private ICfet2Logger logger;
        internal CommunicationManager()
        {
            logger = Cfet2LogManager.GetLogger("CommunicationManager");
        }

        /// <summary>
        /// return the communication module for the protocol
        /// </summary>
        /// <param name="protocol"></param>
        /// <returns></returns>
        public CommunicationModule GetModuleFor(string protocol)
        {
            return moduleDict.ContainsKey(protocol.ToLower()) ? moduleDict[protocol] : null;
        }

        public IEventCommunicationModule GetModuleForEvent(string protocol)
        {
            if (string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase))
                return transportModules.TryGetValue(ActiveEventCommunicationConfiguration.HttpEventSubscriptionTransport, out var selected) ? selected : null;
            return protocol != null && eventModuleDict.TryGetValue(protocol, out var module) ? module : null;
        }

        /// <summary>
        /// add module to the manager, and register the protocol name with the module
        /// </summary>
        /// <param name="module"></param>
        public void TryAddCommunicationModule(CommunicationModule module)
        {
            HubMaster.InjectHubToModule(module);
            foreach (var protocol in module.ProtocolNames)
            {
                try
                {
                    moduleDict.Add(protocol.ToLower(), module);
                }
                catch
                {
                    logger.Error($"Try Add CommunicationModule Protocal {protocol} failed!");
                }
            }
        }

        /// <summary>
        /// add module to the manager, and register the protocol name with the module
        /// </summary>
        /// <param name="module"></param>
        public void TryAddCommunicationModule(IEventCommunicationModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (module.Transport.HasValue && transportModules.ContainsKey(module.Transport.Value))
                throw new InvalidOperationException("An event module is already registered for " + module.Transport.Value);
            var protocols = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var protocol in module.ProtocolNames)
            {
                if (string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(protocol) || !protocols.Add(protocol) || eventModuleDict.ContainsKey(protocol))
                {
                    var message = $"Event protocol '{protocol}' conflicts while registering module '{module.GetType().FullName}'.";
                    logger.Error(message);
                    throw new InvalidOperationException(message);
                }
            }
            // Validate the entire registration before injection or changing either registry.
            HubMaster.InjectHubToModule(module);
            if (module.Transport.HasValue) transportModules.Add(module.Transport.Value, module);
            foreach (var protocol in protocols)
            {
                eventModuleDict.Add(protocol, module);
            }
        }

        internal void StartAll()
        {
            foreach (var item in moduleDict.Values.Distinct())
            {
                item.Start();
            }
            foreach (var item in eventModuleDict.Values.Concat(transportModules.Values).Distinct())
            {
                item.Start();
            }
        }

        /// <summary>
        /// return CommunicationModule,attention:the CommunicationModule is Distinct!
        /// </summary>
        /// <returns></returns>
        internal List<CommunicationModule> GetCommunicationModules()
        {
            var list = moduleDict.Values.Distinct().ToList();
            return list;
        }

        /// <summary>
        /// return IEventCommunicationModule,attention:the IEventCommunicationModule is Distinct!
        /// </summary>
        /// <returns></returns>
        internal List<IEventCommunicationModule> GetEventCommunicationModules()
        {
            var list = eventModuleDict.Values.Concat(transportModules.Values).Distinct().ToList();
            return list;
        }

        public void Dispose()
        {
            foreach (var item in eventModuleDict.Values.Concat(transportModules.Values).Distinct())
            {
                item.Dispose();
            }
        }
    }
}

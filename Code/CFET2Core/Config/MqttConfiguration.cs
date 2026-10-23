using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Jtext103.CFET2.Core.Config
{
    /// <summary>Immutable MQTT settings. Source is an HTTP origin, never a broker address.</summary>
    public sealed class MqttConfiguration
    {
        public string BrokerHost { get; }
        public int BrokerPort { get; }
        public string ClientId { get; }
        public string SourceHttpBaseUri { get; }
        public int KeepAliveSeconds { get; }
        public int DemandRenewIntervalSeconds { get; }
        public int ResourceLeaseTimeoutSeconds { get; }
        public int LeaseScanIntervalSeconds { get; }
        public int OperationTimeoutSeconds { get; }

        public MqttConfiguration(string brokerHost = "localhost", int brokerPort = 1883, string clientId = null,
            string sourceHttpBaseUri = null, int keepAliveSeconds = 30, int demandRenewIntervalSeconds = 300,
            int resourceLeaseTimeoutSeconds = 600, int leaseScanIntervalSeconds = 1, int operationTimeoutSeconds = 10)
        {
            if (string.IsNullOrWhiteSpace(brokerHost) || Uri.CheckHostName(brokerHost) == UriHostNameType.Unknown)
                throw new ArgumentException("Expected a broker hostname/IP without scheme or port.", nameof(brokerHost));
            if (brokerPort < 1 || brokerPort > 65535) throw new ArgumentOutOfRangeException(nameof(brokerPort));
            if (clientId != null && string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Explicit ClientId must not be empty.", nameof(clientId));
            if (sourceHttpBaseUri != null) ValidateSource(sourceHttpBaseUri);
            if (keepAliveSeconds < 0 || keepAliveSeconds > 65535) throw new ArgumentOutOfRangeException(nameof(keepAliveSeconds));
            ValidateTimer(demandRenewIntervalSeconds, nameof(demandRenewIntervalSeconds));
            ValidateTimer(resourceLeaseTimeoutSeconds, nameof(resourceLeaseTimeoutSeconds));
            ValidateTimer(leaseScanIntervalSeconds, nameof(leaseScanIntervalSeconds));
            ValidateTimer(operationTimeoutSeconds, nameof(operationTimeoutSeconds));
            if ((long)resourceLeaseTimeoutSeconds < 2L * demandRenewIntervalSeconds ||
                (long)resourceLeaseTimeoutSeconds - demandRenewIntervalSeconds <= (long)operationTimeoutSeconds + leaseScanIntervalSeconds)
                throw new ArgumentException("Lease must cover twice renewal and leave more than operation timeout plus scan interval.");
            BrokerHost = brokerHost; BrokerPort = brokerPort; ClientId = clientId; SourceHttpBaseUri = sourceHttpBaseUri;
            KeepAliveSeconds = keepAliveSeconds; DemandRenewIntervalSeconds = demandRenewIntervalSeconds;
            ResourceLeaseTimeoutSeconds = resourceLeaseTimeoutSeconds; LeaseScanIntervalSeconds = leaseScanIntervalSeconds;
            OperationTimeoutSeconds = operationTimeoutSeconds;
        }
        private static void ValidateTimer(int seconds, string name)
        {
            if (seconds <= 0 || (long)seconds * 1000 > uint.MaxValue - 1L) throw new ArgumentOutOfRangeException(name);
        }
        private static void ValidateSource(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Any(c => c <= ' ' || c > '~' || c == '\\' || c == '@' || c == '%') ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Port < 1 ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Expected an HTTP origin without credentials, path, query or fragment.", "sourceHttpBaseUri");
            var start = value.IndexOf("://", StringComparison.Ordinal);
            var suffix = value.IndexOfAny(new[] { '/', '?', '#' }, start + 3);
            if (start < 0 || (suffix >= 0 && value.Substring(suffix) != "/")) throw new ArgumentException("Expected an HTTP origin.", "sourceHttpBaseUri");
            var host = uri.Host.Trim('[', ']');
            if (uri.HostNameType == UriHostNameType.IPv6)
            {
                if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) throw new ArgumentException("Invalid source host.");
            }
            else if (Uri.CheckHostName(host) != UriHostNameType.Dns && Uri.CheckHostName(host) != UriHostNameType.IPv4)
                throw new ArgumentException("Invalid source host.");
            if (host.Any(c => "/\\?#@+%".Contains(c))) throw new ArgumentException("Invalid source host.");
        }
        internal MqttConfiguration ResolveSource(string ip, string port, bool publishing)
        {
            var source = SourceHttpBaseUri;
            if (source == null && ip != null && port != null)
            {
                var candidate = ip + ":" + port;
                try { ValidateSource(candidate); source = candidate; }
                catch (ArgumentException) when (!publishing) { }
            }
            if (publishing && source == null) throw new ArgumentException("MQTT publishing requires a valid source HTTP origin.", "MqttSourceHttpBaseUri");
            return new MqttConfiguration(BrokerHost, BrokerPort, ClientId, source, KeepAliveSeconds,
                DemandRenewIntervalSeconds, ResourceLeaseTimeoutSeconds, LeaseScanIntervalSeconds, OperationTimeoutSeconds);
        }
    }
}

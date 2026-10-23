using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Jtext103.CFET2.Core.Config
{
    public static partial class GlobalConfig
    {
        public static string MqttBrokerHost { get; set; } = "localhost";
        public static int MqttBrokerPort { get; set; } = 1883;
        public static string MqttClientId { get; set; } = null;
        public static string MqttSourceHttpBaseUri { get; set; } = null;
        public static int MqttKeepAliveSeconds { get; set; } = 30;
        public static int MqttDemandRenewIntervalSeconds { get; set; } = 300;
        public static int MqttResourceLeaseTimeoutSeconds { get; set; } = 600;
        public static int MqttLeaseScanIntervalSeconds { get; set; } = 1;
        public static int MqttOperationTimeoutSeconds { get; set; } = 10;
        private static MqttConfiguration ConfiguredMqtt() => new MqttConfiguration(MqttBrokerHost, MqttBrokerPort, MqttClientId, MqttSourceHttpBaseUri, MqttKeepAliveSeconds, MqttDemandRenewIntervalSeconds, MqttResourceLeaseTimeoutSeconds, MqttLeaseScanIntervalSeconds, MqttOperationTimeoutSeconds);
        private static void ValidateMqttJsonType(string name, JToken value)
        {
            switch (name)
            {
                case "MqttBrokerHost": if (value.Type == JTokenType.String) return; break;
                case "MqttBrokerPort": if (value.Type == JTokenType.Integer) return; break;
                case "MqttClientId": if (value.Type == JTokenType.String || value.Type == JTokenType.Null) return; break;
                case "MqttSourceHttpBaseUri": if (value.Type == JTokenType.String || value.Type == JTokenType.Null) return; break;
                case "MqttKeepAliveSeconds": if (value.Type == JTokenType.Integer) return; break;
                case "MqttDemandRenewIntervalSeconds": if (value.Type == JTokenType.Integer) return; break;
                case "MqttResourceLeaseTimeoutSeconds": if (value.Type == JTokenType.Integer) return; break;
                case "MqttLeaseScanIntervalSeconds": if (value.Type == JTokenType.Integer) return; break;
                case "MqttOperationTimeoutSeconds": if (value.Type == JTokenType.Integer) return; break;
                default: return;
            }
            throw new ArgumentException("Invalid " + name + ": unexpected JSON type.", name);
        }
        private static MqttConfiguration ParseMqtt(Dictionary<string, string> values) => new MqttConfiguration(
            values.TryGetValue("MqttBrokerHost", out var brokerHost) ? brokerHost : "localhost",
            MqttInteger(values, "MqttBrokerPort", 1883),
            values.TryGetValue("MqttClientId", out var clientId) ? clientId : null,
            values.TryGetValue("MqttSourceHttpBaseUri", out var sourceHttpBaseUri) ? sourceHttpBaseUri : null,
            MqttInteger(values, "MqttKeepAliveSeconds", 30),
            MqttInteger(values, "MqttDemandRenewIntervalSeconds", 300),
            MqttInteger(values, "MqttResourceLeaseTimeoutSeconds", 600),
            MqttInteger(values, "MqttLeaseScanIntervalSeconds", 1),
            MqttInteger(values, "MqttOperationTimeoutSeconds", 10));
        private static int MqttInteger(Dictionary<string, string> values, string name, int fallback)
        {
            if (!values.TryGetValue(name, out var raw)) return fallback;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException("Invalid " + name + ": expected an integer.", name);
            return value;
        }
        private static void ApplyMqtt(MqttConfiguration config)
        {
            MqttBrokerHost = config.BrokerHost;
            MqttBrokerPort = config.BrokerPort;
            MqttClientId = config.ClientId;
            MqttSourceHttpBaseUri = config.SourceHttpBaseUri;
            MqttKeepAliveSeconds = config.KeepAliveSeconds;
            MqttDemandRenewIntervalSeconds = config.DemandRenewIntervalSeconds;
            MqttResourceLeaseTimeoutSeconds = config.ResourceLeaseTimeoutSeconds;
            MqttLeaseScanIntervalSeconds = config.LeaseScanIntervalSeconds;
            MqttOperationTimeoutSeconds = config.OperationTimeoutSeconds;
        }
        private static void AddMqtt(Dictionary<string, string> values)
        {
            values.Add("MqttBrokerHost", MqttBrokerHost);
            values.Add("MqttBrokerPort", MqttBrokerPort.ToString(CultureInfo.InvariantCulture));
            values.Add("MqttClientId", MqttClientId);
            values.Add("MqttSourceHttpBaseUri", MqttSourceHttpBaseUri);
            values.Add("MqttKeepAliveSeconds", MqttKeepAliveSeconds.ToString(CultureInfo.InvariantCulture));
            values.Add("MqttDemandRenewIntervalSeconds", MqttDemandRenewIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            values.Add("MqttResourceLeaseTimeoutSeconds", MqttResourceLeaseTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            values.Add("MqttLeaseScanIntervalSeconds", MqttLeaseScanIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            values.Add("MqttOperationTimeoutSeconds", MqttOperationTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        }
    }
}

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jtext103.CFET2.Core.Config
{
    public static partial class GlobalConfig
    {
        // Configure before creating Core; initialization writes are not concurrent with snapshot reads.
        private static IReadOnlyList<ObservationListRule> autoObserveList = Array.AsReadOnly(new ObservationListRule[0]);
        private static IReadOnlyList<ObservationListRule> autoDenyList = Array.AsReadOnly(new ObservationListRule[0]);

        /// <summary>Configured lists for the next Hub. Updates require restarting the application.</summary>
        public static IReadOnlyList<ObservationListRule> AutoObserveList
        {
            get { return autoObserveList; }
            set { autoObserveList = AutomaticObservationConfiguration.Copy(value); }
        }
        public static IReadOnlyList<ObservationListRule> AutoDenyList
        {
            get { return autoDenyList; }
            set { autoDenyList = AutomaticObservationConfiguration.Copy(value); }
        }

        internal static AutomaticObservationConfiguration CaptureObservationConfiguration()
        {
            return new AutomaticObservationConfiguration(autoObserveList, autoDenyList);
        }

        public static int ResourceSlowGetThresholdMs { get; set; } = 100;

        private const int DefaultLogRetentionDays = 7;
        private const int DefaultResourceObservationIntervalMs = 500;
        private const int DefaultWebSocketResubscribeIntervalMs = 10;

        public static string IP { get; set; }
        public static string Port { get; set; }
        public static string Accept { get; set; }
        public static int LogRetentionDays { get; set; } = DefaultLogRetentionDays;
        public static int ResourceObservationIntervalMs { get; set; } = DefaultResourceObservationIntervalMs;
        public static int WebSocketResubscribeIntervalMs { get; set; } = DefaultWebSocketResubscribeIntervalMs;
        public static Uri HostUri
        {
            get
            {
                if (IP != null && Port != null)
                {
                    return new Uri(IP + ":" + Port);
                }
                else return null;
            }
        }

        public static void Populate(string path)
        {
            var configDict = new Dictionary<string, string>();
            JObject document;
            try { document = JObject.Parse(File.ReadAllText(path, Encoding.Default)); }
            catch (JsonException ex) { throw new ArgumentException("Invalid GlobalConfig JSON: " + ex.Message, nameof(path), ex); }
            foreach (var property in document.Properties())
            {
                var value = property.Value;
                ValidateMqttJsonType(property.Name, value);
                if ((property.Name == "HttpEventSubscriptionTransport" && value.Type != JTokenType.String) ||
                    (property.Name == "WebSocketPortOffset" && value.Type != JTokenType.Integer) ||
                    (property.Name == "EventPublishTransports" && value.Type != JTokenType.Array))
                    throw new ArgumentException("Invalid " + property.Name + ": unexpected JSON type.", property.Name);
                if (property.Name == "ResourceMinDelta" && value.Type != JTokenType.Float && value.Type != JTokenType.Integer)
                    throw new ArgumentException("Invalid ResourceMinDelta: expected a JSON number.", "ResourceMinDelta");
                configDict[property.Name] = value.Type == JTokenType.String ? (string)value :
                    value.Type == JTokenType.Null ? null : value.ToString(Formatting.None);
            }
            Set(configDict);
        }
        public static Dictionary<string, string> Get()
        {
            var configDict = new Dictionary<string, string>();
            configDict.Add("IP", IP);
            configDict.Add("Port", Port);
            configDict.Add("Accept", Accept);
            configDict.Add("LogRetentionDays", LogRetentionDays.ToString());
            configDict.Add("ResourceObservationIntervalMs", ResourceObservationIntervalMs.ToString());
            configDict.Add("WebSocketResubscribeIntervalMs", WebSocketResubscribeIntervalMs.ToString());
            configDict.Add("ResourceSlowGetThresholdMs", ResourceSlowGetThresholdMs.ToString());
            var lists = CaptureObservationConfiguration();
            configDict.Add("AutoObserveList", JsonConvert.SerializeObject(lists.AutoObserveList));
            configDict.Add("AutoDenyList", JsonConvert.SerializeObject(lists.AutoDenyList));
            configDict.Add("ResourceMinDelta", ResourceMinDelta.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            configDict.Add("ResourceMinDeltaOverrides", JsonConvert.SerializeObject(ResourceMinDeltaOverrides));
            configDict.Add("EventPublishTransports", JsonConvert.SerializeObject(EventPublishTransports.Select(x => x.ToString())));
            configDict.Add("HttpEventSubscriptionTransport", HttpEventSubscriptionTransport.ToString());
            configDict.Add("WebSocketPortOffset", WebSocketPortOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddMqtt(configDict);
            return configDict;
        }

        public static void Set(Dictionary<string, string> configDict)
        {
            if (configDict == null) throw new ArgumentNullException(nameof(configDict));
            // Validate both lists and required keys before mutating any configured values.
            var observe = ParseObservationList(configDict, "AutoObserveList");
            var deny = ParseObservationList(configDict, "AutoDenyList");
            var delta = configDict.TryGetValue("ResourceMinDelta", out var deltaRaw) ? ParseMinDelta(deltaRaw, "ResourceMinDelta") : 0;
            var deltaOverrides = ParseMinDeltaOverrides(configDict);
            var communication = ParseEventCommunication(configDict);
            var ip = configDict["IP"];
            var port = configDict["Port"];
            var accept = configDict["Accept"];
            var mqtt = ParseMqtt(configDict);
            mqtt.ResolveSource(ip, port, communication.EventPublishTransports.Contains(EventTransport.Mqtt));
            var slow = configDict.TryGetValue("ResourceSlowGetThresholdMs", out var slowRaw) && int.TryParse(slowRaw, out var slowMs) && slowMs > 0 ? slowMs : 100;
            var retention = configDict.TryGetValue("LogRetentionDays", out var retentionRaw) ? ParseLogRetentionDays(retentionRaw) : DefaultLogRetentionDays;
            var interval = configDict.TryGetValue("ResourceObservationIntervalMs", out var intervalRaw) ? ParseResourceObservationIntervalMs(intervalRaw) : DefaultResourceObservationIntervalMs;
            var resubscribe = configDict.TryGetValue("WebSocketResubscribeIntervalMs", out var resubscribeRaw) ? ParseWebSocketResubscribeIntervalMs(resubscribeRaw) : DefaultWebSocketResubscribeIntervalMs;
            ApplyMqtt(mqtt);
            IP = ip;
            Port = port;
            Accept = accept;
            ResourceSlowGetThresholdMs = slow;
            LogRetentionDays = retention;
            ResourceObservationIntervalMs = interval;
            WebSocketResubscribeIntervalMs = resubscribe;
            autoObserveList = observe;
            autoDenyList = deny;
            resourceMinDelta = delta;
            resourceMinDeltaOverrides = deltaOverrides;
            eventPublishTransports = communication.EventPublishTransports;
            httpEventSubscriptionTransport = communication.HttpEventSubscriptionTransport;
            webSocketPortOffset = communication.WebSocketPortOffset;
        }

        private static IReadOnlyList<ObservationListRule> ParseObservationList(Dictionary<string, string> config, string key)
        {
            if (!config.TryGetValue(key, out var raw) || raw == null)
                return AutomaticObservationConfiguration.Copy(null);
            try
            {
                var token = JToken.Parse(raw);
                if (token.Type == JTokenType.Null) return AutomaticObservationConfiguration.Copy(null);
                if (!(token is JArray array)) throw new ArgumentException("Expected an array.");
                var rules = new List<ObservationListRule>();
                foreach (var entry in array)
                {
                    if (!(entry is JObject item)) throw new ArgumentException("Expected a Path or Regex object.");
                    string path = null, regex = null;
                    foreach (var property in item.Properties())
                    {
                        if (property.Value.Type != JTokenType.String) throw new ArgumentException("Rule values must be strings.");
                        if (property.Name == "Path") path = (string)property.Value;
                        else if (property.Name == "Regex") regex = (string)property.Value;
                        else throw new ArgumentException("Unknown rule field: " + property.Name);
                    }
                    rules.Add(new ObservationListRule(path, regex));
                }
                return AutomaticObservationConfiguration.Copy(rules);
            }
            catch (System.Exception ex) when (ex is ArgumentException || ex is JsonException)
            {
                throw new ArgumentException("Invalid " + key + ": " + ex.Message, key, ex);
            }
        }

        private static int ParseLogRetentionDays(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultLogRetentionDays;
            }

            if (int.TryParse(value, out var days))
            {
                if (days >= 0)
                {
                    return days;
                }
            }

            return DefaultLogRetentionDays;
        }

        private static int ParseResourceObservationIntervalMs(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultResourceObservationIntervalMs;
            }

            if (int.TryParse(value, out var milliseconds))
            {
                if (milliseconds > 0)
                {
                    return milliseconds;
                }
            }

            return DefaultResourceObservationIntervalMs;
        }

        private static int ParseWebSocketResubscribeIntervalMs(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultWebSocketResubscribeIntervalMs;
            }

            if (int.TryParse(value, out var milliseconds) && milliseconds >= 0)
            {
                return milliseconds;
            }

            return DefaultWebSocketResubscribeIntervalMs;
        }
    }
}

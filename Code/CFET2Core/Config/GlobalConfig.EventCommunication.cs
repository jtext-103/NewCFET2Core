using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Jtext103.CFET2.Core.Config
{
    public static partial class GlobalConfig
    {
        private static IReadOnlyList<EventTransport> eventPublishTransports = Array.AsReadOnly(new[] { EventTransport.WebSocket });
        private static EventTransport httpEventSubscriptionTransport = EventTransport.WebSocket;
        private static int webSocketPortOffset = 1;
        public static IReadOnlyList<EventTransport> EventPublishTransports
        {
            get => eventPublishTransports;
            set => eventPublishTransports = EventCommunicationConfiguration.Copy(value);
        }
        public static EventTransport HttpEventSubscriptionTransport
        {
            get => httpEventSubscriptionTransport;
            set { EventCommunicationConfiguration.ValidateTransport(value); httpEventSubscriptionTransport = value; }
        }
        public static int WebSocketPortOffset
        {
            get => webSocketPortOffset;
            set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(WebSocketPortOffset)); webSocketPortOffset = value; }
        }
        internal static EventCommunicationConfiguration CaptureEventCommunicationConfiguration() =>
            new EventCommunicationConfiguration(eventPublishTransports, httpEventSubscriptionTransport, webSocketPortOffset);
        private static EventTransport ParseEventTransport(string raw, string field)
        {
            if (string.Equals(raw, "WebSocket", StringComparison.OrdinalIgnoreCase)) return EventTransport.WebSocket;
            if (string.Equals(raw, "Mqtt", StringComparison.OrdinalIgnoreCase)) return EventTransport.Mqtt;
            throw new ArgumentException("Invalid " + field + ": expected WebSocket or Mqtt.", field);
        }
        private static EventCommunicationConfiguration ParseEventCommunication(Dictionary<string, string> config)
        {
            const string field = "EventPublishTransports";
            IEnumerable<EventTransport> publish = new[] { EventTransport.WebSocket };
            if (config.TryGetValue(field, out var raw))
            {
                try
                {
                    if (!(JToken.Parse(raw ?? "null") is JArray array)) throw new ArgumentException("Expected an array.");
                    publish = array.Select((item, i) => item.Type == JTokenType.String ? ParseEventTransport((string)item, field + "[" + i + "]") :
                        throw new ArgumentException("Expected a transport string at " + field + "[" + i + "]")).ToArray();
                    publish = EventCommunicationConfiguration.Copy(publish);
                }
                catch (System.Exception ex) when (ex is ArgumentException || ex is Newtonsoft.Json.JsonException)
                { throw new ArgumentException("Invalid " + field + ": " + ex.Message, field, ex); }
            }
            var receive = config.TryGetValue("HttpEventSubscriptionTransport", out var receiveRaw) ?
                ParseEventTransport(receiveRaw, "HttpEventSubscriptionTransport") : EventTransport.WebSocket;
            int offset = 1;
            if (config.TryGetValue("WebSocketPortOffset", out var offsetRaw) &&
                (!int.TryParse(offsetRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset <= 0))
                throw new ArgumentException("Invalid WebSocketPortOffset: expected a positive integer.", "WebSocketPortOffset");
            return new EventCommunicationConfiguration(publish, receive, offset);
        }
    }
}

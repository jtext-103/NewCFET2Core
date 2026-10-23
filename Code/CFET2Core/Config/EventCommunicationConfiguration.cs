using System;
using System.Collections.Generic;
using System.Linq;

namespace Jtext103.CFET2.Core.Config
{
    public enum EventTransport { WebSocket, Mqtt }

    /// <summary>Immutable settings captured when Core is created. Changes require a new Core.</summary>
    public sealed class EventCommunicationConfiguration
    {
        public IReadOnlyList<EventTransport> EventPublishTransports { get; }
        public EventTransport HttpEventSubscriptionTransport { get; }
        public int WebSocketPortOffset { get; }
        public MqttConfiguration Mqtt { get; }
        public EventCommunicationConfiguration(IEnumerable<EventTransport> publish, EventTransport receive, int offset)
            : this(publish, receive, offset, new MqttConfiguration()) { }
        public EventCommunicationConfiguration(IEnumerable<EventTransport> publish, EventTransport receive, int offset, MqttConfiguration mqtt)
        {
            Mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
            EventPublishTransports = Copy(publish);
            ValidateTransport(receive);
            if (offset <= 0) throw new ArgumentOutOfRangeException(nameof(offset));
            HttpEventSubscriptionTransport = receive;
            WebSocketPortOffset = offset;
        }
        internal static IReadOnlyList<EventTransport> Copy(IEnumerable<EventTransport> source)
        {
            if (source == null) throw new ArgumentException("EventPublishTransports cannot be null.");
            var values = source.ToArray();
            if (values.Length == 0 || values.Distinct().Count() != values.Length)
                throw new ArgumentException("EventPublishTransports must contain at least one unique transport.");
            foreach (var value in values) ValidateTransport(value);
            return Array.AsReadOnly(values);
        }
        internal static void ValidateTransport(EventTransport value)
        {
            if (value != EventTransport.WebSocket && value != EventTransport.Mqtt)
                throw new ArgumentOutOfRangeException(nameof(value), "Unknown event transport.");
        }
        public int GetWebSocketPort(int httpPort)
        {
            var result = (long)httpPort + WebSocketPortOffset;
            if (httpPort < 1 || httpPort > 65535 || result > 65535)
                throw new ArgumentOutOfRangeException(nameof(httpPort), "HTTP port plus WebSocketPortOffset must be between 1 and 65535.");
            return (int)result;
        }
        /// <summary>Checks required source settings before standard host startup; never falls back to WS.</summary>
        public void EnsureStandardHostSupported()
        {
            if (EventPublishTransports.Contains(EventTransport.Mqtt) && Mqtt.SourceHttpBaseUri == null)
                throw new ArgumentException("MQTT publishing requires a source HTTP origin.");
        }
    }
}

using Jtext103.CFET2.Core.Sample;
using System;

namespace Jtext103.CFET2.Core.Event
{
    public static class ConnectionStatusEvent
    {
        public const string Source = "/sys/connection";
        public const string Disconnected = "disconnected";
        public const string Reconnecting = "reconnecting";
        public const string Connected = "connected";
        public const string Failed = "failed";

        public static EventArg Create(
            string host,
            string protocol,
            string eventType,
            string reason,
            int retryCount,
            DateTime? timestamp = null)
        {
            var normalizedHost = EventHub.NormalizeHost(host);
            var payload = new ConnectionStatusPayload(
                normalizedHost,
                protocol,
                reason,
                timestamp ?? DateTime.UtcNow,
                retryCount);

            return new EventArg(Source, eventType, new Status<ConnectionStatusPayload>(payload), normalizedHost);
        }
    }

    public class ConnectionStatusPayload
    {
        public ConnectionStatusPayload(
            string host,
            string protocol,
            string reason,
            DateTime timestamp,
            int retryCount)
        {
            Host = host;
            Protocol = protocol;
            Reason = reason;
            Timestamp = timestamp;
            RetryCount = retryCount;
        }

        public string Host { get; }

        public string Protocol { get; }

        public string Reason { get; }

        public DateTime Timestamp { get; }

        public int RetryCount { get; }
    }
}

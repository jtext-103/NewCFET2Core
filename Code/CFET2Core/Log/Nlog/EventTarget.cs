using System;
using NLog;
using NLog.Config;
using NLog.Targets;
using System.Collections.Generic;
using System.Text;

namespace Jtext103.CFET2.Core.Log
{
    [Target("EventTarget")]
    public class EventTarget : TargetWithLayout
    {
        public static event EventHandler<string> NlogErrorEvent;

        protected override void Write(LogEventInfo logEvent)
        {
            if (logEvent.Level == LogLevel.Error || logEvent.Level == LogLevel.Fatal)
            {
                string time = logEvent.TimeStamp.ToString("HH:mm:ss");
                string message = logEvent.FormattedMessage;
                string logger = logEvent.LoggerName;
                string level = logEvent.Level.Name;
                string combined = $"At {time}:{logger} {level}-{message}";
                NlogErrorEvent?.Invoke(this, combined);
            }
        }
    }
}

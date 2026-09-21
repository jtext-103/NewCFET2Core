using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jtext103.CFET2.Core.Config
{
    public static class GlobalConfig
    {
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
            JsonConvert.PopulateObject(File.ReadAllText(path, Encoding.Default), configDict);
            try
            {
                IP = configDict["IP"];
                Port = configDict["Port"];
                Accept = configDict["Accept"];
                if (configDict.TryGetValue("LogRetentionDays", out var retentionRaw))
                {
                    LogRetentionDays = ParseLogRetentionDays(retentionRaw);
                }
                else
                {
                    LogRetentionDays = DefaultLogRetentionDays;
                }

                if (configDict.TryGetValue("ResourceObservationIntervalMs", out var intervalRaw))
                {
                    ResourceObservationIntervalMs = ParseResourceObservationIntervalMs(intervalRaw);
                }
                else
                {
                    ResourceObservationIntervalMs = DefaultResourceObservationIntervalMs;
                }

                if (configDict.TryGetValue("WebSocketResubscribeIntervalMs", out var resubscribeIntervalRaw))
                {
                    WebSocketResubscribeIntervalMs = ParseWebSocketResubscribeIntervalMs(resubscribeIntervalRaw);
                }
                else
                {
                    WebSocketResubscribeIntervalMs = DefaultWebSocketResubscribeIntervalMs;
                }
            }
            catch (System.Exception e)
            {
                throw e;
            }
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
            return configDict;
        }

        public static void Set(Dictionary<string, string> configDict)
        {
            try
            {
                IP = configDict["IP"];
                Port = configDict["Port"];
                Accept = configDict["Accept"];
                if (configDict.TryGetValue("LogRetentionDays", out var retentionRaw))
                {
                    LogRetentionDays = ParseLogRetentionDays(retentionRaw);
                }
                else
                {
                    LogRetentionDays = DefaultLogRetentionDays;
                }

                if (configDict.TryGetValue("ResourceObservationIntervalMs", out var intervalRaw))
                {
                    ResourceObservationIntervalMs = ParseResourceObservationIntervalMs(intervalRaw);
                }
                else
                {
                    ResourceObservationIntervalMs = DefaultResourceObservationIntervalMs;
                }

                if (configDict.TryGetValue("WebSocketResubscribeIntervalMs", out var resubscribeIntervalRaw))
                {
                    WebSocketResubscribeIntervalMs = ParseWebSocketResubscribeIntervalMs(resubscribeIntervalRaw);
                }
                else
                {
                    WebSocketResubscribeIntervalMs = DefaultWebSocketResubscribeIntervalMs;
                }
            }
            catch (System.Exception e)
            {
                throw e;
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

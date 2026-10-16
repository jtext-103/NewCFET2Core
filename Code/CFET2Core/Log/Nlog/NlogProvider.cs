using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Config;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using Jtext103.CFET2.Core.Log;

namespace Jtext103.CFET2.Core.Log
{
    public class NlogProvider : ICfet2LogProvider
    {
        private static readonly object retentionScheduleLock = new object();
        private static readonly string[] retentionDateFormats = { "yyyy-MM-dd" };
        private static Timer retentionCleanupTimer;
        private static int isRetentionCleanupRunning = 0;

        public static void Config()
        {
            //配置 Log 输出
            // Step 1. Create configuration object 
            var config = new LoggingConfiguration();

            //输出到文件
            // Step 2. Create targets and add them to the configuration 
            var fileTarget = new FileTarget();
            config.AddTarget("FileTarget", fileTarget);

            // Step 3. Set target properties  
            fileTarget.FileName = "${basedir}/Log_${shortdate}.txt";
            fileTarget.Layout = @"${date} ${logger} ${level} ${newline}${message}";

            // Step 4. Define rules
            var ruleFile = new LoggingRule("*", LogLevel.Debug, fileTarget);
            config.LoggingRules.Add(ruleFile);

            // 加上自定义 error 触发 Target
            var eventTarget = new EventTarget();
            config.AddTarget("eventEvent", eventTarget);
            config.LoggingRules.Add(new LoggingRule("*", LogLevel.Error, eventTarget));

            //输出到控制台
            var consoleTarget = new ConsoleTarget();
            config.AddTarget("ConsoleTarget", consoleTarget);

            consoleTarget.Layout = @"${date} ${logger} ${level} ${newline}${message}";

            var ruleConsole = new LoggingRule("*", LogLevel.Warn, consoleTarget);
            config.LoggingRules.Add(ruleConsole);

            // Step 5. Activate the configuration
            LogManager.Configuration = config;

        }

        public static void StartRetentionScheduler()
        {
            StartRetentionScheduler(RunRetentionCleanupSafe);
        }

        internal static void StartRetentionScheduler(Action cleanup)
        {
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
            lock (retentionScheduleLock)
            {
                var days = GetConfiguredRetentionDays();
                if (days == 0)
                {
                    StopRetentionScheduler();
                    return;
                }

                if (retentionCleanupTimer != null)
                {
                    return;
                }

                // Install the scheduler before releasing the lock, so Stop cannot race a
                // late timer installation. File I/O is performed by the timer, outside it.
                retentionCleanupTimer = new Timer(_ => cleanup(), null, TimeSpan.Zero, TimeSpan.FromDays(1));
            }
        }

        public static void StopRetentionScheduler()
        {
            lock (retentionScheduleLock)
            {
                retentionCleanupTimer?.Dispose();
                retentionCleanupTimer = null;
            }
        }

        public void StartLogCleanup()
        {
            StartRetentionScheduler();
        }

        public void StopLogCleanup()
        {
            StopRetentionScheduler();
        }

        private static void RunRetentionCleanupSafe()
        {
            if (Interlocked.Exchange(ref isRetentionCleanupRunning, 1) == 1)
            {
                return;
            }

            try
            {
                CleanupExpiredLogFiles();
            }
            catch (System.Exception ex)
            {
                InternalLogger.Error(ex, "Failed to cleanup expired log files.");
            }
            finally
            {
                Interlocked.Exchange(ref isRetentionCleanupRunning, 0);
            }
        }

        private static void CleanupExpiredLogFiles()
        {
            var days = GetConfiguredRetentionDays();
            if (days == 0)
            {
                return;
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
            {
                return;
            }

            var files = Directory.GetFiles(baseDir, "Log_*.txt", SearchOption.TopDirectoryOnly);
            var keepFrom = DateTime.Today.AddDays(-(days - 1));
            var deletedCount = 0;
            var skippedCount = 0;

            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!TryGetDateFromFileName(fileName, out var fileDate))
                {
                    skippedCount++;
                    continue;
                }

                if (fileDate < keepFrom)
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                    catch (System.Exception ex)
                    {
                        InternalLogger.Warn(ex, $"Failed to delete expired log file: {file}");
                    }
                }
            }

            InternalLogger.Info($"Log retention cleanup completed. Total={files.Length}, Deleted={deletedCount}, Skipped={skippedCount}, RetentionDays={days}");
        }

        private static bool TryGetDateFromFileName(string fileNameWithoutExtension, out DateTime fileDate)
        {
            fileDate = default(DateTime);
            const string prefix = "Log_";

            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension) || !fileNameWithoutExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var datePart = fileNameWithoutExtension.Substring(prefix.Length);
            return DateTime.TryParseExact(
                datePart,
                retentionDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out fileDate);
        }

        private static int GetConfiguredRetentionDays()
        {
            var days = GlobalConfig.LogRetentionDays;
            if (days < 0)
            {
                return 7;
            }
            return days;
        }

        /// <summary>
        /// get the logger with a spercific name
        /// </summary>
        /// <param name="name"></param>
        public ICfet2Logger GetLogger(string name)
        {
            return new Cfet2Logger(LogManager.GetLogger(name));
        }

    }
}

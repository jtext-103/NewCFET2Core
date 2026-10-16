using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Log;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    public abstract class EventTransportTestBase : CFET2Host
    {
        private Dictionary<string, string> originalConfig = null!;

        [TestInitialize]
        public void CreateCore()
        {
            originalConfig = GlobalConfig.Get();
            GlobalConfig.AutoObserveList = Array.Empty<ObservationListRule>();
            GlobalConfig.AutoDenyList = Array.Empty<ObservationListRule>();
            GlobalConfig.ResourceMinDeltaOverrides = Array.Empty<ResourceMinDeltaOverride>();
            GlobalConfig.ResourceObservationIntervalMs = 60000;
            HubMaster.InjectHubToModule(this);
        }

        [TestCleanup]
        public void DestroyCore()
        {
            Hub.KillMaster();
            GlobalConfig.Set(originalConfig);
        }

        protected static void Await(ManualResetEventSlim signal) => Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(4)), "Timed out waiting for callback.");
        protected static T Field<T>(object owner, string name) => (T)owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        protected static void SetField(object owner, string name, object value) => owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

        protected sealed class RecordingLogger : ICfet2Logger
        {
            public ConcurrentQueue<string> Errors { get; } = new ConcurrentQueue<string>();
            public void Error(string message) => Errors.Enqueue(message);
            public void Trace(string message) { }
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Fatal(string message) { }
        }
    }
}

#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jtext103.CFET2.Core.Communication;
using Jtext103.CFET2.Core.Config;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Sample;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize]
    public class EventCommunicationRoutingTests : CFET2Host
    {
        private Dictionary<string, string> saved;
        private bool mounted;
        [TestInitialize] public void Init()
        {
            saved = GlobalConfig.Get();
            GlobalConfig.EventPublishTransports = new[] { EventTransport.WebSocket };
            GlobalConfig.HttpEventSubscriptionTransport = EventTransport.WebSocket;
            GlobalConfig.WebSocketPortOffset = 1;
        }
        [TestCleanup] public void Cleanup() { if (mounted) Hub.KillMaster(); GlobalConfig.Set(saved); }
        private void Mount() { HubMaster.InjectHubToModule(this); mounted = true; }

        [TestMethod] public void ConfigurationRoundTripDefaultsAndIsolation()
        {
            var transports = new List<EventTransport> { EventTransport.WebSocket, EventTransport.Mqtt };
            GlobalConfig.EventPublishTransports = transports; transports.Clear();
            Assert.AreEqual(2, GlobalConfig.EventPublishTransports.Count);
            var config = GlobalConfig.Get(); config["HttpEventSubscriptionTransport"] = "mQtT";
            config["EventPublishTransports"] = "[\"websocket\",\"MQTT\"]"; config["WebSocketPortOffset"] = "9";
            GlobalConfig.Set(config); Mount();
            var active = MyHub.ActiveEventCommunicationConfiguration;
            GlobalConfig.WebSocketPortOffset = 1; GlobalConfig.HttpEventSubscriptionTransport = EventTransport.WebSocket;
            Assert.AreEqual(9, active.WebSocketPortOffset); Assert.AreEqual(EventTransport.Mqtt, active.HttpEventSubscriptionTransport);
            Assert.AreEqual(8089, active.GetWebSocketPort(8080));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => active.GetWebSocketPort(65530));
            var round = GlobalConfig.Get(); GlobalConfig.Set(round); CollectionAssert.AreEquivalent(new[] { "WebSocket", "Mqtt" }, JArray.Parse(GlobalConfig.Get()["EventPublishTransports"]).Values<string>().ToArray());
            Hub.KillMaster(); mounted = false;
            config.Remove("EventPublishTransports"); config.Remove("HttpEventSubscriptionTransport"); config.Remove("WebSocketPortOffset"); GlobalConfig.Set(config); Mount();
            Assert.AreEqual(1, MyHub.ActiveEventCommunicationConfiguration.WebSocketPortOffset);
            Assert.AreEqual(EventTransport.WebSocket, MyHub.ActiveEventCommunicationConfiguration.HttpEventSubscriptionTransport);
        }

        [TestMethod] public void InvalidConfigurationIsTransactionalAndJsonTypesAreStrict()
        {
            foreach (var field in new[] { "EventPublishTransports", "HttpEventSubscriptionTransport", "WebSocketPortOffset" })
            {
                var invalid = field == "EventPublishTransports" ? new[] { "null", "[]", "[\"WebSocket\",\"websocket\"]", "[1]", "[\"bad\"]" } :
                    field == "HttpEventSubscriptionTransport" ? new[] { null, "1", "bad" } : new[] { null, "0", "-1", "1.5", "2147483648" };
                foreach (var value in invalid)
                {
                    var config = GlobalConfig.Get(); config[field] = value; config["IP"] = "changed";
                    Assert.ThrowsExactly<ArgumentException>(() => GlobalConfig.Set(config));
                    Assert.AreEqual(saved["IP"], GlobalConfig.IP);
                    Assert.AreEqual(1, GlobalConfig.WebSocketPortOffset);
                }
            }
            var path = Path.GetTempFileName();
            try
            {
                var json = new JObject { ["IP"] = "http://127.0.0.1", ["Port"] = "8080", ["Accept"] = "application/json", ["WebSocketPortOffset"] = 5,
                    ["EventPublishTransports"] = new JArray("WebSocket"), ["HttpEventSubscriptionTransport"] = "WebSocket" };
                File.WriteAllText(path, json.ToString()); GlobalConfig.Populate(path); Assert.AreEqual(5, GlobalConfig.WebSocketPortOffset);
                foreach (var field in new[] { "WebSocketPortOffset", "HttpEventSubscriptionTransport", "EventPublishTransports" })
                {
                    var bad = (JObject)json.DeepClone(); bad[field] = field == "HttpEventSubscriptionTransport" ? (JToken)1 : "1";
                    File.WriteAllText(path, bad.ToString()); Assert.ThrowsExactly<ArgumentException>(() => GlobalConfig.Populate(path));
                    Assert.AreEqual(5, GlobalConfig.WebSocketPortOffset);
                }
            }
            finally { File.Delete(path); }
            Assert.ThrowsExactly<ArgumentException>(() => GlobalConfig.EventPublishTransports = null);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GlobalConfig.HttpEventSubscriptionTransport = (EventTransport)99);
        }

        [TestMethod, DataRow(false), DataRow(true)] public void HttpRouteIgnoresRegistrationOrderAndExplicitProtocolsStayIndependent(bool reverse)
        {
            GlobalConfig.HttpEventSubscriptionTransport = EventTransport.Mqtt; Mount();
            var ws = new FakeEvent(EventTransport.WebSocket); var mqtt = new FakeEvent(EventTransport.Mqtt);
            MyHub.TryAddCommunicationModule(reverse ? mqtt : ws); MyHub.TryAddCommunicationModule(reverse ? ws : mqtt);
            GlobalConfig.HttpEventSubscriptionTransport = EventTransport.WebSocket; // creation snapshot wins
            foreach (var path in new[] { "/device/Value", "/device/.*" })
            {
                var filter = new EventFilter("http://source:8000" + path, "changed");
                Assert.AreEqual("mqtt://source:8000", filter.Host);
                using var token = MyHub.Subscribe(filter, _ => { });
                Assert.AreEqual(path, mqtt.LastFilter.SourcesAndTypes[0].Source);
            }
            using (var token = MyHub.Subscribe(new EventFilter { Host = "http://source:8000" }, _ => { })) { }
            Assert.AreEqual(3, mqtt.Subscriptions); Assert.AreEqual(3, mqtt.Unsubscriptions); Assert.AreEqual(0, ws.Subscriptions);
            using (var token = MyHub.Subscribe(new EventFilter("ws://source:9000/device/Value", "changed"), _ => { })) { }
            Assert.AreEqual(1, ws.Subscriptions); Assert.AreEqual("ws://source:9000", ws.LastFilter.Host);
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.TryAddCommunicationModule(new FakeEvent(EventTransport.Mqtt)));
            MyHub.StartCommunication(); Assert.AreEqual(1, ws.Starts); Assert.AreEqual(1, mqtt.Starts);
        }

        [TestMethod] public void ResourceInitialReadUsesHttpAndUnavailableRouteDoesNotRead()
        {
            GlobalConfig.HttpEventSubscriptionTransport = EventTransport.Mqtt; Mount();
            var http = new FakeHttp(); MyHub.TryAddCommunicationModule(http);
            Assert.ThrowsExactly<ProtocolNotSuportedException>(() => MyHub.SubscribeResource("http://source:8000/device/Value/2", _ => { }));
            Assert.AreEqual(0, http.Reads);
            var mqtt = new FakeEvent(EventTransport.Mqtt); MyHub.TryAddCommunicationModule(mqtt);
            using (var subscription = MyHub.SubscribeResource("http://source:8000/device/Value/2", _ => { }))
            { Assert.AreEqual(42, subscription.InitialValue.GetVal<int>()); }
            Assert.AreEqual("http://source:8000/device/Value/2", http.LastUri);
            Assert.AreEqual(1, mqtt.Subscriptions); Assert.AreEqual(1, mqtt.Unsubscriptions);
            Assert.ThrowsExactly<ProtocolNotSuportedException>(() => new EventFilter("unknown://source:8000/device/Value", "changed"));
            Assert.ThrowsExactly<ProtocolNotSuportedException>(() => MyHub.Subscribe(new EventFilter { Host = "unknown://source:8000" }, _ => { }));
        }

        [TestMethod] public void StandardHostRejectsMqttWithoutFallback()
        {
            Mount(); MyHub.ActiveEventCommunicationConfiguration.EnsureStandardHostSupported();
            Hub.KillMaster(); mounted = false; GlobalConfig.EventPublishTransports = new[] { EventTransport.WebSocket, EventTransport.Mqtt }; Mount();
            Assert.ThrowsExactly<NotSupportedException>(() => MyHub.ActiveEventCommunicationConfiguration.EnsureStandardHostSupported());
            Hub.KillMaster(); mounted = false; GlobalConfig.EventPublishTransports = new[] { EventTransport.WebSocket }; GlobalConfig.HttpEventSubscriptionTransport = EventTransport.Mqtt; Mount();
            Assert.ThrowsExactly<NotSupportedException>(() => MyHub.ActiveEventCommunicationConfiguration.EnsureStandardHostSupported());
        }

        [TestMethod] public void ProtocolConflictRejectsEntireRegistrationAndKeepsRoutesUsable()
        {
            Mount();
            var existing = new FakeEvent(null, "WS"); MyHub.TryAddCommunicationModule(existing);
            var rejected = new FakeEvent(EventTransport.WebSocket, "fresh", "ws", "http");
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.TryAddCommunicationModule(rejected));
            StringAssert.Contains(error.Message, "ws");
            Assert.ThrowsExactly<ProtocolNotSuportedException>(() => new EventFilter("http://source:8000/x", "changed"));
            Assert.ThrowsExactly<ProtocolNotSuportedException>(() => new EventFilter("fresh://source:8000/x", "changed"));
            using (var token = MyHub.Subscribe(new EventFilter("ws://source:8000/x", "changed"), _ => { })) { }
            Assert.AreEqual(1, existing.Subscriptions);
            MyHub.StartCommunication(); Assert.AreEqual(0, rejected.Starts);
            // Failed registration must not reserve the transport or an earlier protocol in its list.
            var accepted = new FakeEvent(EventTransport.WebSocket, "fresh", "http"); MyHub.TryAddCommunicationModule(accepted);
            Assert.ThrowsExactly<InvalidOperationException>(() => MyHub.TryAddCommunicationModule(new FakeEvent(null, "other", "OTHER")));
            MyHub.TryAddCommunicationModule(new FakeEvent(null, "other"));
        }

        private sealed class FakeEvent : IEventCommunicationModule
        {
            private readonly string[] protocols;
            public FakeEvent(EventTransport? transport, params string[] protocols)
            {
                Transport = transport;
                this.protocols = protocols.Length == 0 ? new[] { transport == EventTransport.WebSocket ? "ws" : "mqtt", "http" } : protocols;
            }
            public override EventTransport? Transport { get; }
            public override string[] ProtocolNames => protocols;
            public int Subscriptions, Unsubscriptions, Starts; public EventFilter LastFilter;
            public override string GetFullHost(string protocol, string hostname, int port) => (protocol == "http" ? (Transport == EventTransport.WebSocket ? "ws" : "mqtt") : protocol) + "://" + hostname + ":" + port;
            public override void Start() { Starts++; }
            public override void Subscribe(Token token, EventFilter filter) { Subscriptions++; LastFilter = filter; }
            public override void UnSubScribe(Token token) { Unsubscriptions++; }
            public override void RegisterEventCallback(Action<EventArg> callback) { }
            public override void Dispose() { }
        }
        private sealed class FakeHttp : CommunicationModule
        {
            public override string[] ProtocolNames => new[] { "http" };
            public int Reads; public string LastUri;
            private ISample Read(string uri) { Reads++; LastUri = uri; return new Status<int>(42); }
            public override ISample TryGetResourceSampleWithUri(string uri, Dictionary<string, object> inputs) => Read(uri);
            public override ISample TryGetResourceSampleWithUri(string uri, params object[] inputs) => Read(uri);
            public override ISample TryInvokeSampleResourceWithUri(string uri, Dictionary<string, object> inputs) => throw new NotSupportedException();
            public override ISample TryInvokeSampleResourceWithUri(string uri, params object[] inputs) => throw new NotSupportedException();
            public override ISample TrySetResourceSampleWithUri(string uri, Dictionary<string, object> inputs) => throw new NotSupportedException();
            public override ISample TrySetResourceSampleWithUri(string uri, params object[] inputs) => throw new NotSupportedException();
        }
    }
}

#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using Jtext103.CFET2.Core.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass, DoNotParallelize]
    public class MqttConfigurationTests : CFET2Host
    {
        private Dictionary<string, string> saved;
        private bool mounted;
        [TestInitialize] public void Init() { saved = GlobalConfig.Get(); }
        [TestCleanup] public void Cleanup() { if (mounted) Hub.KillMaster(); GlobalConfig.Set(saved); }
        private static Dictionary<string, string> Input() => new Dictionary<string, string>
        { ["IP"] = "http://source-a", ["Port"] = "8000", ["Accept"] = "Json", ["EventPublishTransports"] = "[\"Mqtt\"]" };
        [TestMethod] public void DefaultsResolveCurrentInputAndSnapshotDoesNotReadGlobalsAgain()
        {
            var values = Input(); GlobalConfig.Set(values);
            Assert.IsNull(GlobalConfig.MqttClientId); Assert.IsNull(GlobalConfig.Get()["MqttSourceHttpBaseUri"]);
            HubMaster.InjectHubToModule(this); mounted = true;
            var mqtt = MyHub.ActiveEventCommunicationConfiguration.Mqtt;
            Assert.AreEqual("http://source-a:8000", mqtt.SourceHttpBaseUri);
            Assert.AreEqual("localhost", mqtt.BrokerHost); Assert.AreEqual(1883, mqtt.BrokerPort);
            Assert.AreEqual(300, mqtt.DemandRenewIntervalSeconds); Assert.AreEqual(600, mqtt.ResourceLeaseTimeoutSeconds);
            GlobalConfig.IP = "http://changed"; GlobalConfig.Port = "9000"; GlobalConfig.MqttBrokerPort = 1999;
            Assert.AreEqual("http://source-a:8000", mqtt.SourceHttpBaseUri); Assert.AreEqual(1883, mqtt.BrokerPort);
            var standalone = new EventCommunicationConfiguration(new[] { EventTransport.WebSocket }, EventTransport.WebSocket, 1);
            Assert.IsNull(standalone.Mqtt.SourceHttpBaseUri);
        }
        [TestMethod] public void ExplicitFieldsRoundTripAndSourceOverrideWins()
        {
            var input = Input(); input["MqttBrokerHost"] = "broker"; input["MqttBrokerPort"] = "1884";
            input["MqttClientId"] = "fixed"; input["MqttSourceHttpBaseUri"] = "http://advertised:9000/";
            input["MqttKeepAliveSeconds"] = "0"; input["MqttDemandRenewIntervalSeconds"] = "20";
            input["MqttResourceLeaseTimeoutSeconds"] = "60"; input["MqttLeaseScanIntervalSeconds"] = "2";
            input["MqttOperationTimeoutSeconds"] = "5";
            GlobalConfig.Set(input); var round = GlobalConfig.Get(); GlobalConfig.Set(round);
            foreach (var pair in input) Assert.AreEqual(pair.Value, GlobalConfig.Get()[pair.Key], pair.Key);
            HubMaster.InjectHubToModule(this); mounted = true;
            var mqtt = MyHub.ActiveEventCommunicationConfiguration.Mqtt;
            Assert.AreEqual("http://advertised:9000/", mqtt.SourceHttpBaseUri); Assert.AreEqual("fixed", mqtt.ClientId);
            Assert.AreEqual(0, mqtt.KeepAliveSeconds); Assert.AreEqual(60, mqtt.ResourceLeaseTimeoutSeconds);
        }
        [TestMethod]
        [DataRow("MqttBrokerHost", "http://broker")]
        [DataRow("MqttBrokerPort", "0")]
        [DataRow("MqttBrokerPort", "65536")]
        [DataRow("MqttClientId", " ")]
        [DataRow("MqttSourceHttpBaseUri", "http://source:8000/path")]
        [DataRow("MqttKeepAliveSeconds", "65536")]
        [DataRow("MqttDemandRenewIntervalSeconds", "301")]
        [DataRow("MqttResourceLeaseTimeoutSeconds", "599")]
        [DataRow("MqttLeaseScanIntervalSeconds", "290")]
        [DataRow("MqttOperationTimeoutSeconds", "300")]
        [DataRow("MqttOperationTimeoutSeconds", "4294968")]
        [DataRow("MqttOperationTimeoutSeconds", "1.5")]
        public void InvalidInputDoesNotPartiallyApply(string key, string value)
        {
            GlobalConfig.Set(Input()); var before = JsonConvert.SerializeObject(GlobalConfig.Get());
            var input = Input(); input["IP"] = "http://different"; input[key] = value;
            Assert.Throws<ArgumentException>(() => GlobalConfig.Set(input));
            Assert.AreEqual(before, JsonConvert.SerializeObject(GlobalConfig.Get()));
        }
        [TestMethod] public void InvalidDerivedSourceIsRejectedBeforeAnyAssignment()
        {
            GlobalConfig.Set(Input()); var before = JsonConvert.SerializeObject(GlobalConfig.Get());
            var input = Input(); input["IP"] = "http://source/path";
            Assert.Throws<ArgumentException>(() => GlobalConfig.Set(input));
            Assert.AreEqual(before, JsonConvert.SerializeObject(GlobalConfig.Get()));
        }
        [TestMethod] public void JsonTypesAreStrictAndNullAutomaticFieldsRoundTrip()
        {
            var path = Path.GetTempFileName();
            try
            {
                foreach (var bad in new JToken[] { "1883", 1.5, true, new JArray(), JValue.CreateNull() })
                {
                    var doc = JObject.FromObject(Input()); doc["EventPublishTransports"] = new JArray("Mqtt"); doc["MqttBrokerPort"] = bad;
                    File.WriteAllText(path, doc.ToString()); Assert.Throws<ArgumentException>(() => GlobalConfig.Populate(path));
                }
                var valid = JObject.FromObject(Input()); valid["EventPublishTransports"] = new JArray("Mqtt");
                valid["MqttClientId"] = JValue.CreateNull(); valid["MqttSourceHttpBaseUri"] = JValue.CreateNull();
                File.WriteAllText(path, valid.ToString()); GlobalConfig.Populate(path);
                Assert.IsNull(GlobalConfig.Get()["MqttClientId"]); Assert.IsNull(GlobalConfig.Get()["MqttSourceHttpBaseUri"]);
            }
            finally { File.Delete(path); }
        }
    }
}

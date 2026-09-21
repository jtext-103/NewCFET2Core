using System;
using System.Threading;
using FluentAssertions;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Resource;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jtext103.CFET2.Core.Test.Event
{
    [TestClass]
    public class ResourceEventStage14Test : CFET2Host
    {
        [TestInitialize]
        public void Init()
        {
            HubMaster.InjectHubToModule(this);
        }

        [TestCleanup]
        public void Clean()
        {
            Hub.KillMaster();
        }

        [TestMethod]
        public void StatusMarkedWithCfet2EventShouldExposeNativeEventSupport()
        {
            var thing = new ResourceThing(new NativeEventThing(), "thing", null);

            var resource = (ResourceStatus)thing.Resources["NativeStatus"];

            resource.SupportsNativeEvent.Should().BeTrue();
            resource.Info.SupportsNativeEvent.Should().BeTrue();
        }

        [TestMethod]
        public void ConfigMarkedWithCfet2EventShouldExposeNativeEventSupport()
        {
            var thing = new ResourceThing(new NativeEventThing(), "thing", null);

            var resource = (ResourceConfig)thing.Resources["NativeConfig"];

            resource.SupportsNativeEvent.Should().BeTrue();
            resource.Info.SupportsNativeEvent.Should().BeTrue();
        }

        [TestMethod]
        public void UnmarkedStatusAndConfigShouldNotExposeNativeEventSupport()
        {
            var thing = new ResourceThing(new NativeEventThing(), "thing", null);

            thing.Resources["ObservedStatus"].SupportsNativeEvent.Should().BeFalse();
            thing.Resources["ObservedConfig"].SupportsNativeEvent.Should().BeFalse();
        }

        [TestMethod]
        public void MethodMarkedWithCfet2EventShouldThrowClearImplementationException()
        {
            Action act = () => new ResourceThing(new MethodEventThing(), "thing", null);

            act.Should().Throw<BadThingImplementaionException>()
                .WithMessage("*Cfet2Event can only be used on status or config resources*");
        }

        [TestMethod]
        public void NativeEventThingShouldPublishThroughExistingEventHubPath()
        {
            var thing = new NativeEventThing();
            EventArg received = null!;

            MyHub.TryAddThing(thing, "/", "thing");
            MyHub.EventHub.Subscribe(
                new EventFilter("/thing/NativeStatus", "changed"),
                e => received = e);

            thing.PublishNativeStatus(42);
            Thread.Sleep(500);

            received.Should().NotBeNull();
            received.Source.Should().Be("/thing/NativeStatus");
            received.EventType.Should().Be("changed");
            received.Sample.ObjectVal.Should().Be(42);
        }

        [TestMethod]
        public void Cfet2EventMarkShouldNotChangeGetOrSetBehavior()
        {
            var thing = new ResourceThing(new NativeEventThing(), "thing", null);
            var status = (ResourceStatus)thing.Resources["NativeStatus"];
            var config = (ResourceConfig)thing.Resources["NativeConfig"];

            status.Get().As<Sample.ISample>().ObjectVal.Should().Be(1);
            config.Set(10).As<Sample.ISample>().ObjectVal.Should().Be(10);
            config.Get().As<Sample.ISample>().ObjectVal.Should().Be(10);
        }

        [TestMethod]
        public void SplitConfigMarkedOnGetWithCfet2EventShouldThrowClearImplementationException()
        {
            Action act = () => new ResourceThing(new ConfigEventOnGetThing(), "thing", null);

            act.Should().Throw<BadThingImplementaionException>()
                .WithMessage("*Cfet2Event on config resources must be used on set implementation*");
        }

        [TestMethod]
        public void ConfigNonSetActionMarkedWithCfet2EventShouldThrowClearImplementationException()
        {
            Action act = () => new ResourceThing(new ConfigEventOnInsertThing(), "thing", null);

            act.Should().Throw<BadThingImplementaionException>()
                .WithMessage("*Cfet2Event on config resources must be used on set implementation*");
        }

        [TestMethod]
        public void Cfet2EventWithoutResourceAttributeShouldThrowClearImplementationException()
        {
            Action act = () => new ResourceThing(new EventOnlyThing(), "thing", null);

            act.Should().Throw<BadThingImplementaionException>()
                .WithMessage("*Cfet2Event must be used with Cfet2Status or Cfet2Config*");
        }

        private class NativeEventThing : Thing
        {
            [Cfet2Status]
            [Cfet2Event]
            public int NativeStatus { get; private set; } = 1;

            [Cfet2Config]
            [Cfet2Event]
            public int NativeConfig { get; set; } = 2;

            [Cfet2Status]
            public int ObservedStatus { get; set; } = 3;

            [Cfet2Config]
            public int ObservedConfig { get; set; } = 4;

            public void PublishNativeStatus(int value)
            {
                NativeStatus = value;
                MyHub.PublishResource(GetPathFor(nameof(NativeStatus)), value);
            }
        }

        private class MethodEventThing : Thing
        {
            [Cfet2Method]
            [Cfet2Event]
            public void NativeMethod()
            {
            }
        }

        private class ConfigEventOnGetThing : Thing
        {
            [Cfet2Config(ConfigActions = ConfigAction.Get, Name = "SplitConfig")]
            [Cfet2Event]
            public int SplitConfigGet()
            {
                return 1;
            }

            [Cfet2Config(ConfigActions = ConfigAction.Set, Name = "SplitConfig")]
            public void SplitConfigSet(int value)
            {
            }
        }

        private class EventOnlyThing : Thing
        {
            [Cfet2Event]
            public int EventOnly { get; set; }
        }

        private class ConfigEventOnInsertThing : Thing
        {
            [Cfet2Config(ConfigActions = ConfigAction.Insert)]
            [Cfet2Event]
            public void InsertConfig(int value)
            {
            }
        }
    }
}

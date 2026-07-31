# CFET升级内容

首先CFET升级需要进行底层的升级，不再支持net framework 4.6.1，全部升级到net 6.0往上。目前计划升级到net 10.0，升级后不再支持net framework。

升级包括cfet core 和 cfet app，都需要升级。

## 升级内容

升级内容主要是core改动。主要改动点包括：

1. 新的动作：Subscribe。
2. 新的事件设计。
3. 远程通讯的统一管理。
4. 新的communication module设计。


1. 新的动作：Subscribe

`Subscribe` 表示：

```text
调用方希望持续获得某个资源的变化。
```

与 `Get` 不同，`Subscribe` 是持续性的动作，调用方不需要关心资源是否真的会主动发布事件。Core 内部根据资源能力分成两条路径：

```text
资源带 [CFET2Event]:
  原生事件路径。
  Thing 自己维护变化判断，并自己 EventHub.Publish。
资源不带 [CFET2Event]:
  内部观测路径。
    Core 周期 Get 该资源。
    发现变化后 EventHub.Publish。
```

2. 新的事件设计

与上面讲的一致，新的事件设计需要支持两条路径：

这个事件设计主要关注Status和Config资源的变化事件，Method资源不支持事件。也就是内部的状态和配置变化事件。

```text
原生事件路径:
  资源带 [CFET2Event]。
  Thing 自己维护变化判断，并自己发布事件。
内部观测路径:
  资源不带 [CFET2Event]，服务器加入内部观测列表，
  周期 Get，发现变化后主动发布 event。
```

3. 远程通讯的统一管理

目前 CFET Core 中存在两套远程通讯管理体系：

```text
Hub -> CommunicationManager -> CommunicationModule
EventHub -> EventHub.RemoteEventHubs -> CommunicationModule
```
现在需要统一成一套体系，由 `CommunicationManager` 统一管理所有远程通讯相关的功能，包括资源访问和事件订阅。

```text
Hub -> CommunicationManager -> CommunicationModule
EventHub -> CommunicationManager -> CommunicationModule
```

为了兼容旧 Thing，`EventHub` 即使不再维护远程通讯逻辑，也需要保留 `Subscribe` 和 `Publish` 等遗留接口。以前的 Thing 可能已经在内部使用 `MyHub.EventHub.Subscribe(...)` 进行订阅，或使用 `MyHub.EventHub.Publish(...)` 主动发布事件，升级后这些代码仍然需要可以编译和运行，具体实现可以在 `EventHub` 内部转发到新的订阅和发布体系。

4. 新的communication module设计

为了支持上述的远程通讯统一管理，需要对 `CommunicationModule` 进行升级，新增一个 `IEventCommunicationModule` 接口用于事件订阅相关的功能。这样 `CommunicationManager` 就可以通过接口区分资源访问模块和事件订阅模块，并进行统一管理。

```text

Hub -> CommunicationManager -> CommunicationModule
EventHub -> CommunicationManager -> IEventCommunicationModule
```

同时新增两个CM实现: HTTPCM 和 WebSocketCM，分别负责资源访问和事件订阅的远程通讯。

WebSocketCM升级为远程连接复用，之前是一个事件订阅对应一个WebSocket连接，现在改为同一连接共用一个WebSocket连接。

## 需要确认的问题：

1. 升级的net版本？8.0 或者10.0？已经确认更新到**net10.0**。
2. CFET是否大版本升级，是否需要改名为CFET3？
   - **确认不进行大版本的更新，依旧叫CFET2。新开新的仓库存代码。**

## 升级路径

1. 首先升级CFET的项目版本，将所有项目的target framework升级到net 10.0，Core仍然使用net standard2.0，因为thing是net standard2.0。
2. 升级Core层，按照上面的设计逐步实现新的Subscribe语义、新的事件设计、远程通讯的统一管理和新的communication module设计。
   - 先实现新的远程通讯管理体系，确保资源访问和事件订阅都通过CommunicationManager进行管理。
   - 保留EventHub的旧事件接口，例如 `MyHub.EventHub.Subscribe(...)` 和 `MyHub.EventHub.Publish(...)`，用于兼容已有Thing。
   - 实现两个新的CommunicationModule：HTTPCM和WebSocketCM，分别负责资源访问和事件订阅的远程通讯。
   - 再实现新的Subscribe语义和事件设计，确保Thing可以通过Subscribe持续获得资源变化事件。
3. 升级App层，这里应该没什么需要修改的，清理一下文件什么的。
4. 升级完成后进行全面测试，确保功能正常。
5. 测试现有thing的适配程度。
6. 升级statemachine适配当前subscribe语义，这个也单开仓库。

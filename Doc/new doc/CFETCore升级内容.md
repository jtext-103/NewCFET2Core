# CFETCore升级内容

## 当前实现与阅读边界（2026-09-21）

本文保留升级目标和实施步骤，步骤中的“建议/需要”不表示该能力仍未实现。当前已实现 Hub 资源订阅、远程初始 Get、WebSocket 服务端资源订阅、资源观测和重连重订阅；测试覆盖与端到端验证应分别说明，本文不作为本次测试通过记录。

- 未标记 `[Cfet2Event]` 的资源仅支持标量自动观测，复杂类型或无法确定标量类型的声明会拒绝资源订阅；完整类型列表见 [Core README](../../README.md#资源自动观测的类型边界)。
- 原生资源须显式发布；`GetPathFor(nameof(资源成员))` 解析成员特性中的名称和 Thing 实例路径。分离的 Config 方法须在 Set 实现上标记 `[Cfet2Event]`。
- 远程 `SubscribeResource` 返回只确认初始 Get 与本地订阅注册，不等待远端成功 ACK；后续可能进入 Failed，须查询 WebSocket 诊断或日志。初始读取与注册之间不存在原子性保证。
- EventHub 关闭行为和配置边界以 [Core README](../../README.md) 为准。

CFETCore 是本次 CFET 升级中的主要改动部分。整体目标是在保持 Core 框架兼容性的基础上，升级通信、事件和订阅模型，并统一资源访问、事件订阅和远程通讯的管理方式。

本次 CFET 升级后不再支持 .NET Framework 4.6.1，CFET 项目整体升级到 `net10.0`。但 CFETCore 仍然保持 `netstandard2.0`，因为 Thing 仍然是 `netstandard2.0`。

## 升级目标

1. 保持 CFETCore 项目的 target framework 为 `netstandard2.0`。
2. 新增资源动作：`Subscribe`。
3. 重做事件设计，支持资源变化事件。
4. 统一远程通讯管理，由 `CommunicationManager` 管理资源访问和事件订阅。
5. 升级 `CommunicationModule` 设计，补充事件订阅通信接口。
6. 新增 HTTP 和 WebSocket 两类通信模块职责划分。

## 1. 目标框架

CFETCore 当前仍然使用：

```text
netstandard2.0
```

原因是 Thing 仍然是 `netstandard2.0`，Core 需要继续保持对 Thing 的兼容。

CFET 项目整体升级时需要检查：

1. `CFET2Core.csproj` 的 `TargetFramework`。
2. Core 继续保持 `netstandard2.0`。
3. App、测试项目和其他外围项目升级到 `net10.0`。
4. `net10.0` 项目引用 `netstandard2.0` Core 的兼容性。
5. 已有 Thing、CommunicationModule、EventHub 相关代码是否依赖旧框架行为。

## 2. 新的资源级订阅 API：Subscribe

新增 `SubscribeResource` API，用于表示：

```text
调用方希望持续获得某个资源的变化。
```

`SubscribeResource` 与 `Get` 的区别在于：

1. `Get` 是一次性读取当前资源值。
2. `SubscribeResource` 是持续性动作。
3. 资源级 `Subscribe` 建立时应先返回一次当前资源值，后续变化再通过事件推送。
4. 调用方不需要关心资源是否具备主动发布事件的能力。
5. Core 负责根据资源能力选择事件来源。

推荐接口：

```csharp
public ResourceSubscription SubscribeResource(
    string resourceUri,
    Action<EventArg> handler,
    string eventType = "changed")
```

`SubscribeResource` 不加入 `AccessAction`，不复用 `TryAccessResourceSampleWithUri(...)`，因为订阅需要回调和 Token 生命周期。资源值仍统一使用 `ISample`：初始值通过 `ResourceSubscription.InitialValue` 返回，后续事件值通过 `EventArg.Sample` 推送。

`SubscribeResource` 的成功条件是“初始 `Get` 成功 + 事件订阅成功”。订阅入口必须先拿到有效 `InitialValue`，再注册事件订阅；如果初始 `Get` 失败、返回 null 或返回 invalid sample，则认为订阅失败并抛错，不保留本地订阅、远程订阅或内部观测状态。远程资源也是同一策略：初始 `Get` 失败通常表示资源不可达、协议不可用、资源不存在或网络不通，因此不支持“订阅成功但 `InitialValue` 为 invalid”的半成功状态。

当前已落地的调用方式：

```csharp
ISample currentValue;

using var subscription = MyHub.SubscribeResource(
    "/thing/Temperature",
    e =>
    {
        currentValue = e.Sample;
    });

currentValue = subscription.InitialValue;
```

普通事件订阅和发布不再建议业务代码直接持有 `EventHub`，可以使用 Hub 层包装：

```csharp
var token = MyHub.Subscribe(new EventFilter("/thing/status", "changed"), handler);
MyHub.Publish("/thing/status", "changed", value);
MyHub.Publish(new EventArg("/thing/status", "changed", sample));
MyHub.PublishResource("/thing/status", value);
MyHub.Unsubscribe(token);
```

Thing 内部主动发布资源变化事件时，应优先使用 `MyHub.PublishResource(GetPathFor(nameof(资源成员)), value)`。`PublishResource` 默认事件类型为 `changed`；`GetPathFor` 会把 C# 成员名解析为 `[Cfet2Status(Name = "...")]` 或 `[Cfet2Config(Name = "...")]` 声明的对外资源名；如果没有显式 `Name`，则使用成员名。这样同一 Thing 类型多实例挂载时，事件会带上各自的 Thing 路径。

`EventHub.Subscribe(...)`、`EventHub.Publish(...)` 和 `EventHub.Unsubscribe(...)` 仍保留为旧 Thing 兼容入口。

Core 内部需要支持两条订阅路径：

```text
资源带 [Cfet2Event]:
  原生事件路径。
  Thing 自己维护变化判断，并使用 MyHub.PublishResource(GetPathFor(...), value) 发布。
  Core 不进行内部观测，避免重复发布。

资源不带 [Cfet2Event]:
  内部观测路径。
  用于兼容旧 Thing。
  Core 周期 Get 该资源。
  发现变化后 EventHub.Publish。
```

这里的 `Subscribe` 语义面向 Status 和 Config 资源。Method 资源不支持事件订阅。

## 3. 新的事件设计

新的事件设计主要关注 Status 和 Config 资源的变化事件，也就是 Thing 内部状态和配置的变化。

事件来源分为两类：

```text
原生事件路径:
  资源带 [Cfet2Event]。
  Thing 自己维护变化判断，并自己发布事件。
  Core 不观测、不自动发布。

内部观测路径:
  资源不带 [Cfet2Event]。
  用于兼容旧 Thing。
  服务器加入内部观测列表。
  周期 Get。
  发现变化后主动发布 event。
```

需要实现的 Core 能力：

1. 标识资源是否支持原生事件。
2. 为不支持原生事件的资源建立内部观测机制。
3. 对观测到的资源变化统一调用 `EventHub.Publish`。
4. 事件过滤和订阅逻辑继续由 `EventHub` 负责。
5. Method 资源不进入事件系统。
6. 资源级订阅返回初始值和后续事件 Token。
7. 订阅入口先获取初始值，再注册事件订阅，避免调用方设置初始值时覆盖已经到达的事件。
8. 初始值获取失败时，订阅整体失败并清理已建立的中间状态。

当前测试覆盖：

1. `[Cfet2Event]` 资源只接收 Thing 主动发布，不被 Core 内部观测。
2. 未标记 `[Cfet2Event]` 的 Status/Config 可以被 Core 周期观测，变化后发布 `changed`。
3. 多个订阅者订阅同一未标记资源时只建立一份观测。
4. 初始 `Get` 失败时抛错，并且不保留订阅或观测状态。
5. WebSocketCM 服务端收到远端订阅时，存在的资源走 `ResourceSubscription`，不存在的 source 回退普通事件订阅；存在但不可资源订阅的 Method 或非法事件类型会拒绝订阅。

## 4. 远程通讯统一管理

升级前，CFETCore 中远程资源访问和远程事件订阅是两套管理体系：

```text
Hub -> CommunicationManager -> CommunicationModule
EventHub -> EventHub.RemoteEventHubs -> CommunicationModule
```

当前实现已统一为：

```text
Hub -> CommunicationManager -> CommunicationModule
EventHub -> CommunicationManager -> CommunicationModule
```

也就是说，所有远程通讯相关能力都由 `CommunicationManager` 统一管理，包括：

1. 远程资源访问。
2. 远程事件订阅。
3. 通信模块注册。
4. 通信模块选择。
5. 连接生命周期管理。

`EventHub` 不再直接维护独立的远程事件 Hub 列表，而是通过 `CommunicationManager` 完成远程订阅相关操作。

兼容性要求：

1. `EventHub` 即使不再维护远程通讯逻辑，也必须保留旧接口。
2. 旧 Thing 可能已经在内部使用 `MyHub.EventHub.Subscribe(...)` 进行事件订阅，或使用 `MyHub.EventHub.Publish(...)` 主动发布事件，这类代码升级后仍然需要可用。
3. `EventHub.Subscribe(...)` 和 `EventHub.Publish(...)` 都需要作为遗留兼容入口保留，内部实现可以再转发到新的事件订阅、发布体系或 `CommunicationManager`。
4. 新代码推荐使用新的 `Subscribe` 语义和统一通讯管理体系，但不能破坏旧 Thing 的编译和运行兼容性。

## 5. CommunicationModule升级

为了支持远程通讯统一管理，需要升级 `CommunicationModule` 体系。

原有资源访问链路：

```text
Hub -> CommunicationManager -> CommunicationModule
```

新增事件订阅链路：

```text
EventHub -> CommunicationManager -> IEventCommunicationModule
```

需要新增一个事件通信接口：

```text
IEventCommunicationModule
```

该接口用于表达通信模块支持远程事件订阅能力。这样 `CommunicationManager` 可以通过接口区分：

1. 负责资源访问的 `CommunicationModule`。
2. 负责事件订阅的 `IEventCommunicationModule`。

## 6. 新的通信模块职责

新增两个通信模块实现：

```text
HTTPCM
WebSocketCM
```

职责划分：

1. `HTTPCM` 负责远程资源访问。
2. `WebSocketCM` 负责远程事件订阅。

`WebSocketCM` 需要升级为远程连接复用：

```text
旧设计:
  一个事件订阅对应一个 WebSocket 连接。

新设计:
  同一远程连接共用一个 WebSocket 连接。
```

这样可以减少连接数量，并让远程事件订阅由 `CommunicationManager` 统一维护。

## 升级路径

1. 升级 CFET 项目版本。
   - App、测试项目和外围项目的 target framework 升级到 `net10.0`。
   - CFETCore 仍然保持 `netstandard2.0`，因为 Thing 是 `netstandard2.0`。
2. 升级远程通讯管理体系。
   - 将远程资源访问和远程事件订阅统一收敛到 `CommunicationManager`。
   - 移除或替换 `EventHub.RemoteEventHubs` 这类独立远程事件管理路径。
   - 保留 `EventHub.Subscribe(...)` 和 `EventHub.Publish(...)` 等旧接口作为遗留兼容入口，避免旧 Thing 中的 `MyHub.EventHub.Subscribe(...)`、`MyHub.EventHub.Publish(...)` 失效。
3. 升级 `CommunicationModule` 设计。
   - 新增 `IEventCommunicationModule`。
   - 实现 `HTTPCM` 和 `WebSocketCM`。
   - 让 `CommunicationManager` 能根据协议和能力选择对应模块。
4. 实现新的 `SubscribeResource` 语义。
   - Status 和 Config 支持订阅。
   - Method 不支持订阅。
   - 支持原生事件路径和内部观测路径。
5. 实现新的事件设计。
   - 支持 `[Cfet2Event]` 标识的原生事件。
   - 无 `[Cfet2Event]` 的旧资源由 Core 内部周期观测，默认周期为 500ms，可通过 `GlobalConfig.ResourceObservationIntervalMs` 配置。
   - 支持无事件资源的周期观测。
   - 统一通过 `EventHub.Publish` 发布资源变化。
6. 测试现有 Thing 的适配程度。
7. 测试远程资源访问和远程事件订阅。
8. 测试 WebSocket 连接复用。
9. 升级 statemachine 以适配当前 `Subscribe` 语义，该部分单独开仓库处理。

## 需要确认的问题

1. 内部观测路径的周期、比较方式和资源变化判定规则需要进一步确定。
2. `IEventCommunicationModule` 的接口方法需要结合现有 `IRemoteEventHub`、`EventFilter`、`Token` 和 `EventArg` 设计确定。
3. WebSocket 连接复用需要明确连接键：按 host、protocol、port，还是按完整远程端点。

# CFETCore升级内容

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

## 2. 新的资源动作：Subscribe

新增 `Subscribe` 动作，用于表示：

```text
调用方希望持续获得某个资源的变化。
```

`Subscribe` 与 `Get` 的区别在于：

1. `Get` 是一次性读取当前资源值。
2. `Subscribe` 是持续性动作。
3. 调用方不需要关心资源是否具备主动发布事件的能力。
4. Core 负责根据资源能力选择事件来源。

Core 内部需要支持两条订阅路径：

```text
资源带 [CFET2Event]:
  原生事件路径。
  Thing 自己维护变化判断，并自己 EventHub.Publish。

资源不带 [CFET2Event]:
  内部观测路径。
  Core 周期 Get 该资源。
  发现变化后 EventHub.Publish。
```

这里的 `Subscribe` 语义面向 Status 和 Config 资源。Method 资源不支持事件订阅。

## 3. 新的事件设计

新的事件设计主要关注 Status 和 Config 资源的变化事件，也就是 Thing 内部状态和配置的变化。

事件来源分为两类：

```text
原生事件路径:
  资源带 [CFET2Event]。
  Thing 自己维护变化判断，并自己发布事件。

内部观测路径:
  资源不带 [CFET2Event]。
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

## 4. 远程通讯统一管理

当前 CFETCore 中远程资源访问和远程事件订阅是两套管理体系：

```text
Hub -> CommunicationManager -> CommunicationModule
EventHub -> EventHub.RemoteEventHubs -> CommunicationModule
```

升级后需要统一为：

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
4. 实现新的 `Subscribe` 语义。
   - Status 和 Config 支持订阅。
   - Method 不支持订阅。
   - 支持原生事件路径和内部观测路径。
5. 实现新的事件设计。
   - 支持 `[CFET2Event]` 标识的原生事件。
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

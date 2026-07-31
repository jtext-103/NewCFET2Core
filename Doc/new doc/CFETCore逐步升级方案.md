# CFETCore逐步升级方案

本文档用于指导 CFETCore 的逐步升级顺序，并列出每一步需要重点测试的内容。

核心原则：

1. CFETCore 继续保持 `netstandard2.0`。
2. App、测试项目和外围项目升级到 `net10.0`。
3. 先保证旧 Thing 可编译、可运行，再逐步替换内部实现。
4. `MyHub.EventHub.Subscribe(...)` 和 `MyHub.EventHub.Publish(...)` 必须保留兼容。
5. 新的远程通讯统一收敛到 `CommunicationManager`。
6. 事件系统由 `EventHub` 统一负责事件语义、订阅关系、过滤、分发、异常隔离和 Token 生命周期。
7. `IEventCommunicationModule` 只负责远程事件通信，不直接持有或调用业务 handler。
8. 本地事件和远程事件必须分表分发，避免同名事件互相触发。
9. 远程事件必须携带 Host/Endpoint，并在分发时进行 Host 隔离。

## 阶段1：项目框架和基础编译

目标：

1. 确认 CFETCore 继续使用 `netstandard2.0`。
2. 将 App、测试项目和外围项目升级到 `net10.0`。
3. 建立升级过程中的基础编译和测试基线。

升级内容：

1. 保持 `CFET2Core.csproj` 的 `TargetFramework` 为 `netstandard2.0`。
2. 将测试项目、App 项目和其他外围项目的 `TargetFramework` 升级到 `net10.0`。
3. 更新 NuGet 包版本，优先选择同时兼容 `netstandard2.0` 和 `net10.0` 的版本。
4. 先不修改 Core 行为，只确认新框架组合可以编译。

测试重点：

1. Core 项目单独编译通过。
2. `net10.0` 项目可以正常引用 `netstandard2.0` 的 Core。
3. 现有测试项目可以编译通过。
4. 现有 Thing 不需要修改代码即可编译。
5. 检查反射、动态加载、配置文件读取、日志初始化是否受框架升级影响。

风险点：

1. NuGet 包升级后 API 发生变化。
2. `net10.0` 项目引用 `netstandard2.0` Core 时出现依赖版本冲突。
3. 旧 Thing 间接依赖 .NET Framework 特有 API。

## 阶段2：锁定 EventHub 兼容接口

目标：

1. 保留旧 Thing 使用的事件接口。
2. 给后续事件系统重构提供稳定外壳。
3. 明确旧入口不等于旧内部实现，后续可以在内部转入新事件模型。

必须保留的接口：

```text
MyHub.EventHub.Subscribe(...)
MyHub.EventHub.Publish(...)
EventHub.Subscribe(...)
EventHub.Publish(...)
EventHub.Unsubscribe(...)
Token.Dispose()
```

升级内容：

1. 明确 `EventHub` 仍然是旧 Thing 的事件入口。
2. 保持 `Token`、`EventFilter`、`EventArg` 的现有基本用法。
3. 不要求旧 Thing 修改事件订阅和发布代码。
4. 后续即使内部转发到 `CommunicationManager` 和新订阅表，外部入口也保持不变。
5. 标记旧远程 handler 直调路径为待迁移路径。

测试重点：

1. 旧 Thing 内部调用 `MyHub.EventHub.Subscribe(...)` 可以正常订阅。
2. 旧 Thing 内部调用 `MyHub.EventHub.Publish(...)` 可以正常发布。
3. `Subscribe` 返回的 `Token` 仍然可用于取消订阅。
4. `Unsubscribe` 后不再收到事件。
5. 同一个事件有多个订阅者时，所有匹配订阅者都能收到事件。
6. 正则匹配的 `EventFilter` 行为不变。
7. 事件大小写匹配规则不变。
8. handler 抛异常时不影响其他订阅者。
9. 高频 `Publish` 时事件队列不丢事件、不死锁。

## 阶段3：重构 CommunicationManager 能力边界

目标：

1. 让 `CommunicationManager` 成为远程通讯的统一入口。
2. 同时管理资源访问通信模块和事件通信模块。
3. 保证同一协议下资源通信模块和事件通信模块互不覆盖。

升级内容：

1. 保留现有资源访问通信模块管理能力。
2. 在 `CommunicationManager` 中新增事件通信模块管理能力。
3. 引入并注册 `IEventCommunicationModule`。
4. `CommunicationManager.GetModuleForEvent(protocol)` 返回事件通信模块。
5. `CommunicationManager.GetModuleFor(protocol)` 返回资源访问通信模块。
6. `Hub` 对外提供事件通信模块注册入口。
7. `HubMaster.InjectHubToModule(...)` 能给 `IEventCommunicationModule` 注入 `MyHub`。
8. 启动和释放事件通信模块时，同一个模块多协议注册不能被重复 Start 或 Dispose。

建议链路：

```text
资源访问:
  Hub -> CommunicationManager -> CommunicationModule

事件订阅:
  EventHub -> CommunicationManager -> IEventCommunicationModule
```

测试重点：

1. 原有远程资源访问行为不变。
2. 多个 `CommunicationModule` 注册时，协议选择正确。
3. 不支持协议时错误信息清晰。
4. 重复注册同一协议时行为明确。
5. 事件通信模块和资源访问模块可以独立注册。
6. 同一协议同时存在资源访问和事件订阅能力时不会互相覆盖。
7. `CommunicationManager` 生命周期管理正确，启动和释放都能调用到模块。
8. `DummyEventCommunicationModule` 可以被注册、注入、启动和释放。

风险点：

1. 原有 `GetMouduleFor` 拼写和使用路径较多，重构时不要破坏旧调用。
2. 通信模块既可能只支持资源访问，也可能同时支持事件订阅。
3. 错误地把本地事件也走远程通讯路径，会导致性能和行为变化。

## 阶段4：补齐事件模型基础字段

目标：

1. 为远程事件 Host 隔离和远程取消订阅打基础。
2. 让远程事件能明确表达来源端点。

升级内容：

1. 在 `EventArg` 中增加远程来源字段，建议为：

```csharp
public string Host { get; set; }
```

2. 明确 `Host` 在远程事件中表示事件来源 Host，本地事件可以为空。
3. 在 `Token` 中增加 Host 字段：

```csharp
public string Host { get; }
```

4. 远程 Token 创建时写入 `Protocol` 和 `Host`。
5. 增加 Host 规范化工具，输出统一格式：

```text
protocol://host:port
```

测试重点：

1. 本地 `EventArg.Host` 为空时不影响本地事件。
2. 远程 Token 包含 `Protocol` 和 `Host`。
3. Host 大小写、默认端口、尾部斜杠的规范化行为明确。
4. 旧的本地事件测试全部通过。

风险点：

1. Host 规范化不一致会导致远程订阅无法匹配。
2. `Token` 构造函数变更不能破坏旧本地 token。

## 阶段5：调整 IEventCommunicationModule 接口

目标：

1. 让事件通信模块只负责通信，不再持有业务 handler。
2. 为远程事件回流到 `EventHub.ReceiveRemoteEvent(...)` 做准备。

接口调整：

从：

```csharp
public abstract void Subscribe(Token token, EventFilter filter, Action<EventArg> handler);
public abstract void UnSubScribe(Token token);
```

调整为：

```csharp
public abstract void Subscribe(Token token, EventFilter filter);
public abstract void UnSubScribe(Token token);
public abstract void RegisterEventCallback(Action<EventArg> callback);
```

说明：

1. `Subscribe(Token token, EventFilter filter)` 只建立远程订阅，不接收业务 handler。
2. `UnSubScribe(Token token)` 取消远程订阅。
3. `RegisterEventCallback(...)` 用于注册远程事件回流入口。
4. 通信模块收到远程事件后调用注册的 callback。
5. callback 目标由 `EventHub` 提供，通常是 `EventHub.ReceiveRemoteEvent(...)`。

可选方案：

```csharp
public event Action<EventArg> OnEventReceived;
```

短期建议使用 `RegisterEventCallback(...)`，因为它与当前抽象类风格一致，改动范围更小。

测试重点：

1. 事件通信模块不再接收业务 handler。
2. `DummyEventCommunicationModule` 可以注册 callback 并模拟远程事件回流。
3. 通信模块 Dispose 后不再触发 callback。
4. 旧 Thing 的 `EventHub.Subscribe(...)` 调用方式不变。

风险点：

1. 所有现有事件通信模块实现都需要调整。
2. 如果遗漏某个调用点，远程事件可能仍绕过 `EventHub`。

## 阶段6：EventHub 分离本地订阅和远程订阅

目标：

1. 从结构上保证本地事件和远程事件互不触发。
2. 新增远程事件统一回流入口。

升级内容：

1. 将当前单一订阅表拆成：

```text
localSubDict:
  Token.Id -> Subscriber(filter, handler)

remoteSubDict:
  Token.Id -> Subscriber(filter, handler)
```

2. `EventHub.Subscribe(...)` 判断：

```text
filter.Host 为空 -> localSubDict
filter.Host 非空 -> remoteSubDict
```

3. `EventHub.Publish(...)` 只分发 `localSubDict`。
4. 新增 `EventHub.ReceiveRemoteEvent(EventArg eventArg)`。
5. `ReceiveRemoteEvent(...)` 只分发 `remoteSubDict`。
6. `EventHub.Unsubscribe(...)` 根据 `token.IsRemote` 删除对应字典。
7. 远程订阅失败时清理 `remoteSubDict` 中的半注册状态。

测试重点：

1. 本地事件不会触发远程订阅。
2. 远程事件不会触发本地订阅。
3. 本地和远程存在相同 `source/eventType` 时互不影响。
4. 远程 Token.Dispose 后不再收到远程事件。
5. 远程订阅失败后 `remoteSubDict` 不保留 token。

风险点：

1. 拆分订阅表时可能破坏本地事件旧行为。
2. 远程订阅失败如果不清理，会留下无法取消的半注册状态。

## 阶段7：远程事件 Host 隔离

目标：

1. 防止不同远程主机的同名事件串台。
2. 确保远程事件必须匹配订阅 Host 后才触发 handler。

升级内容：

1. 远程订阅时规范化 `filter.Host`。
2. 远程 Token 保存规范化 Host。
3. 订阅端 `IEventCommunicationModule` 接收远程事件后写入可信 `eventArg.Host`。
4. `EventHub.ReceiveRemoteEvent(...)` 分发前校验：

```text
eventArg.Host 非空
normalized(eventArg.Host) == normalized(filter.Host)
```

5. Host 校验通过后再执行 source/eventType 匹配。

测试重点：

1. Host-A 和 Host-B 订阅相同 `source/eventType`。
2. Host-A 事件只触发 Host-A 订阅。
3. Host-B 事件只触发 Host-B 订阅。
4. `Host` 缺失时不触发任何远程订阅。
5. Host 大小写或默认端口写法按规范化结果匹配。

风险点：

1. 远端事件中的 Host 不可信，必须由订阅端通信模块补齐或校验。
2. Host 规范化规则如果分散实现，会出现难以定位的匹配失败。

## 阶段8：远端本地事件到订阅端回流

目标：

实现完整的远程事件本质链路：订阅端订阅远端本地事件，远端本地事件触发后经 `IEventCommunicationModule` 回流订阅端。

升级内容：

远程订阅建立：

```text
订阅端 EventHub.Subscribe(remoteFilter, handler)
  -> 保存 remoteSubDict
  -> 订阅端 IEventCommunicationModule.Subscribe(token, filter)
  -> 订阅端 IEventCommunicationModule 与远端 IEventCommunicationModule 建立订阅
```

远端事件触发：

```text
远端 Thing
  -> 远端 EventHub.Publish(localEvent)
  -> 远端 IEventCommunicationModule 注册的本地 handler 被触发
  -> 远端 IEventCommunicationModule 发送 EventArg 给订阅端
```

订阅端回流：

```text
订阅端 IEventCommunicationModule
  -> 接收 EventArg
  -> 设置 Host
  -> RegisterEventCallback 回调 EventHub.ReceiveRemoteEvent
```

测试重点：

1. Dummy 模块模拟远端本地事件触发。
2. 订阅端通过 `ReceiveRemoteEvent(...)` 收到事件。
3. 通信模块不直接调用业务 handler。
4. 多个远程订阅共享同一远程通信连接时，所有匹配订阅者收到事件。

风险点：

1. 远端 IEventCommunicationModule 订阅远端本地事件时，不能造成递归远程订阅。
2. 远程事件回流不能调用 `Publish(...)`。

## 阶段9：取消订阅和半失败清理

目标：

保证远程取消订阅不会留下本地半注册状态或通信模块映射泄漏。

升级内容：

1. `EventHub.Unsubscribe(token)` 对远程 Token：

```text
调用 eventModule.UnSubScribe(token)
移除 remoteSubDict
移除 remoteRouteTable
```

2. `IEventCommunicationModule.UnSubScribe(token)` 必须立即清理本地映射：

```text
Token.Id -> remote subscription id
Token.Id -> endpoint
Token.Id -> connection subscription entry
```

3. 远端取消报文发送失败时：

```text
本地映射仍然销毁
失败交由通信模块记录、重试或连接释放时清理
```

4. `Unsubscribe` 和 `Token.Dispose()` 必须幂等。

测试重点：

1. 远程 Token.Dispose 后不再收到远程事件。
2. 同一个 Token 多次 Dispose 不抛异常。
3. 远端取消报文失败时，本地 `remoteSubDict` 和模块映射都被清理。
4. 高频远程事件回流时取消订阅不抛集合修改异常。

风险点：

1. 如果通信模块不清理本地映射，会造成内存泄漏。
2. 如果 EventHub 不清理 remoteSubDict，会导致已释放 handler 继续被触发。

## 阶段10：连接状态事件

目标：

让业务层感知远程连接状态，而不是只能通过“没有数据”推断断线。

升级内容：

1. 事件通信模块在连接状态变化时生成系统事件：

```text
source = /sys/connection
eventType = disconnected
eventType = reconnecting
eventType = connected
eventType = failed
```

2. 状态事件必须包含：

```text
Host
Protocol
Reason
Timestamp
RetryCount
```

3. 状态事件通过 `EventHub.ReceiveRemoteEvent(...)` 进入远程分发路径。
4. 状态事件只触发对应 Host 的远程订阅者。

测试重点：

1. 模拟断线，业务层收到 `disconnected`。
2. 模拟重连开始，业务层收到 `reconnecting`。
3. 模拟重连成功，业务层收到 `connected`。
4. 模拟失败，业务层收到 `failed`。
5. Host-A 的连接状态事件不触发 Host-B 订阅。

风险点：

1. 连接状态事件不能进入本地 `Publish(...)` 路径。
2. 连接状态事件必须同样进行 Host 隔离。

## 阶段11：路由表和性能优化

目标：

避免大量订阅时每个事件全量遍历 `localSubDict` 或 `remoteSubDict`。

升级内容：

保留订阅字典：

```text
localSubDict:
  Token.Id -> Subscriber

remoteSubDict:
  Token.Id -> Subscriber
```

新增路由表：

```text
localRouteTable:
  routeKey(source, eventType) -> List<Subscriber>

remoteRouteTable:
  routeKey(host, source, eventType) -> List<Subscriber>
```

路由表职责：

1. 事件到达时快速定位候选订阅者。
2. 候选订阅者仍执行最终 `Predicate`。
3. 正则表达式 filter 可以进入较宽泛候选组。
4. `Token.Dispose()` 时同步从路由表移除。

测试重点：

1. 普通精确匹配走路由表。
2. 正则匹配行为不变。
3. 大量订阅下事件分发仍稳定。
4. 取消订阅后路由表不残留订阅者。

风险点：

1. 路由表优化不能改变正则匹配语义。
2. 路由表和订阅字典必须保持一致。

## 阶段12：线程安全和异常隔离

目标：

保证事件分发、远程回流和取消订阅并发时不发生集合修改异常或半触发状态。

升级内容：

1. 订阅字典使用 `ConcurrentDictionary` 或受控锁。
2. 分发前对候选订阅列表做快照。
3. 或使用 Copy-On-Write 路由表。
4. handler 调用前再次确认 token 未取消。
5. 单个 handler 异常不影响其他 handler。
6. Host 缺失或 Host 不匹配的远程事件应丢弃并记录。

测试重点：

1. 远程事件高速回流时取消订阅。
2. 本地事件高速 Publish 时取消订阅。
3. handler 抛异常时其他 handler 仍执行。
4. `ReceiveRemoteEvent(...)` 不因单个 handler 异常中断。
5. Host 缺失的远程事件不触发订阅。

风险点：

1. 当前字典遍历如果与删除并发，可能触发集合修改异常。
2. 异步 handler 的异常需要被隔离和记录。

## 阶段13：实现新的通信模块

目标：

1. 用 `HTTPCM` 负责远程资源访问。
2. 用 `WebSocketCM` 负责远程事件订阅。
3. WebSocket 连接支持复用。
4. 真实通信模块遵守新的事件回流设计。

升级内容：

1. 实现 `HTTPCM`，用于 `Get`、`Set`、`Call` 等资源访问动作。
2. 实现 `WebSocketCM`，用于远程事件订阅、取消订阅和事件接收。
3. `WebSocketCM` 内部维护远程连接池。
4. 连接复用键建议使用规范化后的 `protocol + host + port`。
5. 订阅数量为 0 时释放或延迟释放 WebSocket 连接。
6. 远程事件到达后只调用 `RegisterEventCallback` 注册的回调，不直接调用业务 handler。
7. 断线、重连、失败状态通过连接状态事件通知业务层。

测试重点：

1. HTTP 资源访问可以正常 Get、Set、Call。
2. WebSocket 可以完成远程 Subscribe。
3. 多个订阅指向同一远程端点时只建立一个 WebSocket 连接。
4. 多个远程端点会建立不同连接。
5. 取消某个订阅不会影响同连接上的其他订阅。
6. 最后一个订阅取消后连接释放策略正确。
7. WebSocket 断线后重连策略明确。
8. 远程事件回流经过 `EventHub.ReceiveRemoteEvent(...)`。
9. 连接关闭时所有相关 `Token` 状态一致。

风险点：

1. 连接复用容易造成订阅路由混乱。
2. 取消订阅时可能误关共享连接。
3. 断线重连后远程订阅需要恢复或明确失败。

## 阶段14：新增 CFET2Event 和原生事件路径

目标：

1. 用 `[CFET2Event]` 标识支持原生事件的资源。
2. 允许 Thing 自己判断变化并主动发布事件。

升级内容：

1. 新增 `[CFET2Event]` Attribute。
2. 资源扫描时识别 Status 和 Config 上的 `[CFET2Event]`。
3. 带 `[CFET2Event]` 的资源走原生事件路径。
4. Thing 通过 `MyHub.EventHub.Publish(...)` 主动发布本地事件。
5. Method 资源不支持事件。

测试重点：

1. Status 上标注 `[CFET2Event]` 后可以被识别。
2. Config 上标注 `[CFET2Event]` 后可以被识别。
3. Method 上标注事件时应拒绝、忽略或给出明确错误。
4. Thing 主动 `Publish` 后订阅方可以收到事件。
5. 事件 source、event type、payload/sample 内容正确。
6. 标注不影响原有 Get、Set、Call 行为。

风险点：

1. Attribute 命名必须统一为 `CFET2Event`。
2. 原生事件由 Thing 自己判断变化，Core 不应重复发布。
3. 旧 Thing 没有标注该 Attribute，不能因此行为异常。

## 阶段15：实现内部观测路径

目标：

1. 对不带 `[CFET2Event]` 的 Status 和 Config 提供周期观测。
2. 发现资源变化后由 Core 主动发布本地事件。

升级内容：

1. 建立内部观测列表。
2. 订阅不带 `[CFET2Event]` 的资源时，将资源加入观测列表。
3. 周期调用 `Get` 获取资源当前值。
4. 比较新旧值，发生变化后调用 `EventHub.Publish`。
5. 无订阅者后从观测列表移除资源。

测试重点：

1. 不带 `[CFET2Event]` 的 Status 可以被周期观测。
2. 不带 `[CFET2Event]` 的 Config 可以被周期观测。
3. Method 不会进入观测列表。
4. 值未变化时不重复发布事件。
5. 值变化时只发布预期次数的事件。
6. 多个订阅者订阅同一资源时只建立一份观测任务。
7. 所有订阅者取消后观测任务停止。
8. Get 抛异常时观测任务不会崩溃，并能记录错误。
9. 慢速 Get 不会阻塞整个事件系统。
10. 观测周期可配置，并有合理默认值。

风险点：

1. Sample 比较方式不明确会导致误报或漏报。
2. 高频观测可能带来性能问题。
3. 观测任务生命周期管理不当会造成后台任务泄漏。
4. Config 的 Set 后是否立即触发变化事件需要明确。

## 阶段16：引入资源级 Subscribe 语义

目标：

1. 将 `Subscribe` 建模为资源动作。
2. 调用方只表达持续获得变化，不关心事件来源。

升级内容：

1. Status 和 Config 支持 `Subscribe`。
2. Method 明确不支持 `Subscribe`。
3. Subscribe 内部根据资源能力选择原生事件路径或内部观测路径。
4. 对远程资源的 Subscribe 通过 `CommunicationManager` 和 `IEventCommunicationModule` 完成。
5. 保持旧 `EventHub.Subscribe(...)` 兼容，同时推荐新语义。

测试重点：

1. 对带 `[CFET2Event]` 的 Status Subscribe，走原生事件路径。
2. 对不带 `[CFET2Event]` 的 Status Subscribe，走内部观测路径。
3. 对带 `[CFET2Event]` 的 Config Subscribe，走原生事件路径。
4. 对不带 `[CFET2Event]` 的 Config Subscribe，走内部观测路径。
5. 对 Method Subscribe 返回明确错误。
6. 本地资源 Subscribe 正常。
7. 远程资源 Subscribe 正常。
8. 取消 Subscribe 后本地订阅、远程订阅、内部观测都能清理。
9. Subscribe 不影响已有 Get、Set、Call。

风险点：

1. 新 Subscribe 语义和旧 EventHub Subscribe 名称相近，需要文档和 API 边界清楚。
2. 本地订阅、远程订阅、内部观测三种路径的 Token 生命周期要统一。
3. 同一路径被旧接口和新接口同时订阅时不能重复发布异常事件。

## 阶段17：清理旧远程事件实现

目标：

1. 在新体系稳定后，清理旧的远程事件内部实现。
2. 保留外部兼容接口。

升级内容：

1. 移除通信模块直接保存业务 handler 的实现。
2. 移除旧的远程 handler 直调路径。
3. 保留 `EventHub.Subscribe(...)`、`EventHub.Publish(...)`、`EventHub.Unsubscribe(...)`。
4. 旧接口内部统一转发到新实现。
5. 更新文档和示例代码。

测试重点：

1. 删除旧路径后所有本地事件测试仍通过。
2. 删除旧路径后所有远程事件测试仍通过。
3. 旧 Thing 不改代码仍可编译运行。
4. 旧接口和新接口混用时行为一致。
5. Dispose 后不会留下后台线程、队列、WebSocket 连接。

风险点：

1. 过早删除旧路径会破坏旧 Thing。
2. 旧接口内部转发不完整会导致远程订阅取消失败。
3. EventHub 生命周期和 CommunicationManager 生命周期可能互相引用，需要避免释放顺序问题。

## 阶段18：现有 Thing 和 StateMachine 适配

目标：

1. 验证已有 Thing 的适配程度。
2. 为 statemachine 单独仓库升级提供输入。

升级内容：

1. 收集现有 Thing 对 `EventHub.Subscribe`、`EventHub.Publish`、资源 Get/Set/Call 的使用方式。
2. 找出需要标注 `[CFET2Event]` 的资源。
3. 找出适合使用内部观测路径的资源。
4. 记录 statemachine 对 Subscribe 语义的适配点。

测试重点：

1. 旧 Thing 不修改代码即可运行。
2. 旧 Thing 主动发布本地事件行为不变。
3. 旧 Thing 内部订阅事件行为不变。
4. 新 Thing 可以使用 `[CFET2Event]` 和新 Subscribe 语义。
5. statemachine 订阅状态变化时不会重复触发。
6. statemachine 取消订阅时资源观测和远程订阅都能释放。

风险点：

1. 不同 Thing 对事件 source 和 event type 的命名可能不统一。
2. statemachine 可能依赖旧事件触发顺序。
3. 内部观测路径可能改变状态变化的触发时机。

## 最小可交付切片

### V1：远程事件语义闭环

包含：

1. `EventArg.Host`
2. `Token.Host`
3. 新 `IEventCommunicationModule` 接口
4. `remoteSubDict`
5. `ReceiveRemoteEvent(...)`
6. Dummy 远程事件回流测试
7. Host 隔离测试

不包含：

1. 路由表优化
2. 连接状态事件
3. 真实 WebSocketCM

### V2：生命周期和稳定性

包含：

1. 远程取消订阅半失败清理
2. 幂等 Dispose
3. 并发取消订阅测试
4. handler 异常隔离测试
5. 连接状态事件

### V3：性能和真实通信

包含：

1. localRouteTable
2. remoteRouteTable
3. WebSocketCM 连接复用
4. 断线重连
5. 远端订阅恢复或失败状态通知

## 全量回归测试清单

框架和兼容性：

1. Core 保持 `netstandard2.0`。
2. App、测试项目和外围项目使用 `net10.0`。
3. 旧 Thing 不改代码可编译。
4. 旧 Thing 不改代码可运行。

本地事件：

1. `EventHub.Publish` 正常发布本地事件。
2. `EventHub.Subscribe` 正常订阅本地事件。
3. `EventHub.Unsubscribe` 正常取消本地订阅。
4. `Token.Dispose` 正常取消本地订阅。
5. 多订阅者、多事件类型、多资源路径匹配正确。
6. handler 异常不影响事件循环。
7. 高频事件不丢失、不死锁。

远程事件：

1. 远程订阅通过 `CommunicationManager` 选择 `IEventCommunicationModule`。
2. 远程事件通过 `ReceiveRemoteEvent(...)` 回流。
3. 远程事件只触发远程订阅。
4. 本地事件只触发本地订阅。
5. Host-A 和 Host-B 同名事件互不触发。
6. 缺少 `Host` 的远程事件不触发订阅。
7. 远程 Token.Dispose 后不再收到远程事件。
8. 远程订阅失败不留下半注册状态。

Subscribe 语义：

1. Status 支持 Subscribe。
2. Config 支持 Subscribe。
3. Method 不支持 Subscribe。
4. 原生事件路径和内部观测路径选择正确。
5. 取消订阅后资源观测停止。

远程通讯：

1. HTTP 资源访问正常。
2. WebSocket 远程事件订阅正常。
3. WebSocket 连接复用正常。
4. 远程取消订阅正常。
5. 网络异常和重连行为明确。
6. 连接状态事件可以通知业务层。

生命周期：

1. Host 启动时通信模块和事件模块初始化正确。
2. Host 停止时通信模块、事件队列、观测任务、WebSocket 连接全部释放。
3. 多次启动和停止不会残留线程或连接。
4. 重复 Dispose 不抛异常。
5. 远程取消失败不造成本地映射泄漏。

性能和稳定性：

1. 大量本地事件发布。
2. 大量远程事件回流。
3. 大量内部观测资源。
4. 慢速资源 Get。
5. 资源 Get 抛异常。
6. handler 执行慢或抛异常。
7. 高频事件分发不依赖全量订阅遍历。
8. 事件分发中取消订阅不抛集合修改异常。

## 推荐实施顺序汇总

1. 建立框架升级基线，Core 保持 `netstandard2.0`。
2. 锁定 `EventHub.Subscribe`、`EventHub.Publish`、`EventHub.Unsubscribe` 兼容接口。
3. 扩展 `CommunicationManager`，加入事件通信模块管理能力。
4. 补齐 `EventArg.Host` 和 `Token.Host`。
5. 调整 `IEventCommunicationModule` 接口，移除业务 handler 参数。
6. 拆分 `localSubDict` 和 `remoteSubDict`，新增 `ReceiveRemoteEvent(...)`。
7. 实现远程事件 Host 隔离。
8. 打通远端本地事件到订阅端的回流链路。
9. 完善远程取消订阅和半失败清理。
10. 增加连接状态事件。
11. 增加路由表和线程安全能力。
12. 实现 `HTTPCM` 和 `WebSocketCM`。
13. 新增 `[CFET2Event]`，实现原生事件路径。
14. 实现无 `[CFET2Event]` 资源的内部观测路径。
15. 引入资源级 `Subscribe` 语义。
16. 清理旧远程事件内部实现，但保留旧外部接口。
17. 做现有 Thing 和 statemachine 适配测试。

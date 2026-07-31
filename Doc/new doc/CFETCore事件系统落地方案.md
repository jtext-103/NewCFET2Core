# CFETCore 事件系统落地方案

本文档基于《CFETCore 事件处理总体设计》，用于指导事件系统从当前实现逐步落地到新设计。落地目标是：保持旧 Thing 的 `EventHub.Subscribe(...)`、`EventHub.Publish(...)` 兼容，同时将远程事件订阅、回流、分发和生命周期管理统一收敛到 `EventHub` 与 `IEventCommunicationModule` 的新职责边界中。

## 落地原则

1. 先保证旧本地事件行为不变，再迁移远程事件。
2. 先做接口和数据结构，再改通信模块行为。
3. 远程通信模块不再持有业务 handler。
4. 远程事件回流只进入 `EventHub.ReceiveRemoteEvent(...)`。
5. 本地事件和远程事件分表管理，互不触发。
6. 远程事件必须带 Host，并且 Host 必须参与匹配。
7. 每一步都要有测试保护，不能依赖手工联调判断行为。

## 当前基础

当前代码已经具备以下基础：

1. `CFET2Core` 保持 `netstandard2.0`。
2. 测试项目已升级到 `net10.0`。
3. `CommunicationManager` 已经可以注册资源通信模块和事件通信模块。
4. `IEventCommunicationModule` 已存在。
5. `EventHub.Subscribe(...)` 已经能根据 `filter.Host` 走远程事件通信模块。
6. 已有 `DummyEventCommunicationModule` 测试覆盖阶段 3 的注册、启动、释放和远程订阅转发。

当前主要缺口：

1. `IEventCommunicationModule.Subscribe(...)` 仍接收业务 handler。
2. 远程事件没有统一回流到 `EventHub.ReceiveRemoteEvent(...)`。
3. `EventHub` 没有分离 `localSubDict` 和 `remoteSubDict`。
4. `EventArg` 没有明确的远程来源 Host 字段。
5. `Token` 缺少 Host。
6. 没有远程 Host 隔离测试。
7. 没有连接状态事件。
8. 没有路由表和高并发取消订阅保护。

## 阶段 0：冻结兼容基线

目标：

建立当前行为的安全网，确保后续重构不破坏旧 Thing。

实施内容：

1. 保留 `EventHub.Subscribe(...)`、`EventHub.Publish(...)`、`EventHub.Unsubscribe(...)` 的公开入口。
2. 保留现有本地事件队列行为。
3. 保留现有 `EventFilter.Predicate(...)` 的 source/eventType 匹配行为。
4. 确认现有 53 个测试全部通过。

测试重点：

1. 本地 `Subscribe + Publish` 行为不变。
2. `Token.Dispose()` 取消本地订阅行为不变。
3. handler 异常不影响其他 handler。
4. 高频本地事件不丢事件、不死锁。

完成标准：

1. 不改旧本地事件 API。
2. 全量测试通过。

## 阶段 1：补齐事件模型基础字段

目标：

为远程事件 Host 隔离和远程取消订阅打基础。

实施内容：

1. 在 `EventArg` 中增加远程来源字段，建议命名：

```csharp
public string RemoteHost { get; set; }
```

2. 明确 `RemoteHost` 只用于远程事件，本地事件可以为空。
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

1. 本地 `EventArg.RemoteHost` 为空时不影响本地事件。
2. 远程 Token 包含 `Protocol` 和 `Host`。
3. Host 大小写、默认端口、尾部斜杠的规范化行为明确。

完成标准：

1. `EventArg` 能表达远程来源。
2. `Token` 能表达远程订阅对应的 Host。
3. 现有本地测试不受影响。

## 阶段 2：调整 IEventCommunicationModule 接口

目标：

让事件通信模块只负责通信，不再持有业务 handler。

实施内容：

将接口从：

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

或者采用 C# event：

```csharp
public event Action<EventArg> OnEventReceived;
```

推荐先使用 `RegisterEventCallback(...)`，因为它和当前抽象类风格一致，改动范围更小。

实施步骤：

1. 修改 `IEventCommunicationModule` 抽象接口。
2. 修改 `DummyEventCommunicationModule`。
3. 修改所有调用 `eventModule.Subscribe(token, filter, handler)` 的地方。
4. 在事件模块注册或启动时注册回调：

```csharp
eventModule.RegisterEventCallback(EventHub.ReceiveRemoteEvent);
```

测试重点：

1. 事件通信模块不再接收业务 handler。
2. 注册 callback 后，Dummy 模块可以模拟远程事件回流。
3. 通信模块 Dispose 后不再触发回调。

完成标准：

1. 所有 `IEventCommunicationModule.Subscribe(...)` 实现不再持有业务 handler。
2. 远程事件回调入口已统一注册到 `EventHub`。

## 阶段 3：EventHub 分离本地订阅和远程订阅

目标：

从结构上保证本地事件和远程事件互不触发。

实施内容：

1. 将当前单一 `subDict` 拆成：

```csharp
localSubDict:
  Token.Id -> Subscriber

remoteSubDict:
  Token.Id -> Subscriber
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

测试重点：

1. 本地事件不会触发远程订阅。
2. 远程事件不会触发本地订阅。
3. 本地和远程存在相同 `source/eventType` 时互不影响。
4. 远程 Token.Dispose 后不再收到远程事件。

完成标准：

1. 本地事件和远程事件分发路径完全分离。
2. `ReceiveRemoteEvent(...)` 成为远程事件唯一入口。

## 阶段 4：远程事件 Host 隔离

目标：

防止不同远程主机的同名事件串台。

实施内容：

1. 远程订阅时规范化 `filter.Host`。
2. 远程 Token 保存规范化 Host。
3. 订阅端 `IEventCommunicationModule` 接收远程事件后写入可信 `eventArg.RemoteHost`。
4. `EventHub.ReceiveRemoteEvent(...)` 分发前校验：

```text
eventArg.RemoteHost 非空
normalized(eventArg.RemoteHost) == normalized(filter.Host)
```

5. Host 校验通过后再执行 source/eventType 匹配。

测试重点：

1. Host-A 和 Host-B 订阅相同 `source/eventType`。
2. Host-A 事件只触发 Host-A 订阅。
3. Host-B 事件只触发 Host-B 订阅。
4. `RemoteHost` 缺失时不触发任何远程订阅。
5. Host 大小写或默认端口写法按规范化结果匹配。

完成标准：

1. 远程事件必须基于 Host 隔离。
2. 不同 Host 同名事件不会互相触发。

## 阶段 5：远端本地事件到订阅端回流

目标：

实现完整的远程事件本质链路：订阅端订阅远端本地事件，远端本地事件触发后经 IEventCommunicationModule 回流订阅端。

实施内容：

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
  -> 设置 RemoteHost
  -> RegisterEventCallback 回调 EventHub.ReceiveRemoteEvent
```

测试重点：

1. Dummy 模块模拟远端本地事件触发。
2. 订阅端通过 `ReceiveRemoteEvent(...)` 收到事件。
3. 通信模块不直接调用业务 handler。
4. 多个远程订阅共享同一远程通信连接时，所有匹配订阅者收到事件。

完成标准：

1. 远程事件回流由 `EventHub` 统一分发。
2. 远程通信模块只负责远程通信和事件回调。

## 阶段 6：取消订阅和半失败清理

目标：

保证远程取消订阅不会留下本地半注册状态或通信模块映射泄漏。

实施内容：

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

完成标准：

1. 取消订阅不会导致 handler 继续触发。
2. 取消失败不会造成内存泄漏。

## 阶段 7：连接状态事件

目标：

让业务层感知远程连接状态，而不是只能通过“没有数据”推断断线。

实施内容：

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
RemoteHost
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

完成标准：

1. 业务层能订阅并感知远程连接状态。
2. 连接状态事件同样遵守 Host 隔离。

## 阶段 8：路由表和性能优化

目标：

避免大量订阅时每个事件全量遍历 `localSubDict` 或 `remoteSubDict`。

实施内容：

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

完成标准：

1. 高频事件分发不依赖全量订阅遍历。
2. 路由表不改变旧匹配语义。

## 阶段 9：线程安全和异常隔离

目标：

保证事件分发、远程回流和取消订阅并发时不发生集合修改异常或半触发状态。

实施内容：

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

完成标准：

1. 并发事件分发稳定。
2. 异常隔离行为和本地/远程路径一致。

## 阶段 10：清理旧远程事件路径

目标：

在新路径稳定后，清理旧的远程 handler 直调模式。

实施内容：

1. 移除所有 `IEventCommunicationModule.Subscribe(token, filter, handler)` 调用。
2. 移除通信模块保存业务 handler 的实现。
3. 保留 `EventHub.Subscribe(...)` 旧入口。
4. 旧入口内部转为：

```text
本地订阅 -> localSubDict
远程订阅 -> remoteSubDict + IEventCommunicationModule.Subscribe(token, filter)
```

5. 更新示例和文档。

测试重点：

1. 旧 Thing 不改代码可以继续订阅本地事件。
2. 旧 Thing 不改代码可以继续订阅远程事件。
3. 远程事件不再绕过 `EventHub` 调用业务 handler。
4. 所有新旧事件测试通过。

完成标准：

1. 旧公开接口兼容。
2. 内部远程事件路径统一到新设计。

## 建议实施顺序

推荐按以下顺序开发：

1. 阶段 0：冻结兼容基线。
2. 阶段 1：补 `EventArg.RemoteHost` 和 `Token.Host`。
3. 阶段 2：调整 `IEventCommunicationModule` 接口。
4. 阶段 3：拆分 `localSubDict` 和 `remoteSubDict`，增加 `ReceiveRemoteEvent(...)`。
5. 阶段 4：实现 Host 隔离。
6. 阶段 5：打通远程事件回流。
7. 阶段 6：完善取消订阅和半失败清理。
8. 阶段 7：增加连接状态事件。
9. 阶段 8：增加路由表性能优化。
10. 阶段 9：补线程安全和异常隔离。
11. 阶段 10：清理旧远程 handler 直调路径。

不要提前实现真实 WebSocket 连接复用。建议先用 `DummyEventCommunicationModule` 把事件语义测完整，再落地真实 `WebSocketCM`。

## 最小可交付切片

为了降低风险，可以拆成三个可交付版本。

### V1：远程事件语义闭环

包含：

1. `EventArg.RemoteHost`
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

## 回归测试清单

本地事件：

1. 本地订阅只收到本地 `Publish`。
2. 本地 `Token.Dispose()` 后不再收到本地事件。
3. 多个本地订阅者都能收到匹配事件。
4. 本地 handler 抛异常不影响其他 handler。

远程事件：

1. 远程订阅只收到 `ReceiveRemoteEvent(...)` 回流事件。
2. 远程事件不触发本地订阅。
3. 本地事件不触发远程订阅。
4. Host-A 和 Host-B 同名事件互不触发。
5. 缺少 `RemoteHost` 的事件不触发远程订阅。
6. Host 规范化匹配正确。

生命周期：

1. 远程订阅失败不留下 `remoteSubDict`。
2. 远程取消失败仍清理本地映射。
3. 重复 Dispose 不抛异常。
4. Dispose 后通信模块不再回调已释放 `EventHub`。

连接状态：

1. 断线事件可被业务层感知。
2. 重连事件可被业务层感知。
3. 失败事件可被业务层感知。
4. 连接状态事件按 Host 隔离。

性能和并发：

1. 高频远程事件回流稳定。
2. 高频本地事件发布稳定。
3. 事件分发中取消订阅不抛集合修改异常。
4. 路由表删除无残留。

## 风险和处理策略

1. 接口变更影响已有事件通信模块。
   - 处理：先改 Dummy 模块并补测试，再迁移真实模块。

2. Host 规范化不一致导致事件无法匹配。
   - 处理：集中实现 Host 规范化工具，并用测试覆盖大小写、端口、尾斜杠。

3. 拆分订阅表破坏本地事件。
   - 处理：先保留本地路径原逻辑，只新增远程路径。

4. 远程取消订阅失败造成泄漏。
   - 处理：规定 `UnSubScribe(token)` 必须先清本地映射，再处理远端报文。

5. 路由表优化改变正则匹配语义。
   - 处理：路由表只做候选筛选，最终仍执行 `Predicate`。

6. 连接状态事件被普通业务事件误匹配。
   - 处理：状态事件固定使用 `/sys/connection`，并必须带 Host。


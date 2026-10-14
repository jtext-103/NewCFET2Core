# CFETCore升级内容

## 当前实现与阅读边界（2026-10-14）

本文记录当前工作区的 Core 升级内容，并区分已实现能力、通信接入边界和已知缺陷。当前已实现单资源订阅、远程初始 Get、标量及受支持容器资源自动观察、Core 正则组合订阅及共享观察句柄、自动观察列表、公共比较与主动变化发布、minDelta 和宿主通信选择配置。现有 WebSocket 精确资源订阅和重连重订阅已存在；Core 正则观察现已接入 WS 服务端，并增加异步准备及 Core 慢 Get 监测。源码实现状态不代表已发布 NuGet 版本，Core 测试与跨模块端到端验收分别说明。

- 未标记 `[Cfet2Event]` 且 getter 无参数的资源支持标量、数组、精确 `List<T>` 和 `Dictionary<TKey, TValue>` 自动观测。单资源 `SubscribeResource` 对不支持类型抛错；正则组合 `SubscribeResources` 对该资源返回 `Skipped`，其余资源和事件订阅继续。完整类型列表见 [Core README](../../README.md#资源自动观测的类型边界)。
- **所有含参数 getter 均不自动观察**，包括默认参数和 params。精确 SubscribeResource 已支持按传入地址读取初值并接收对应路径主动事件；具体行为和正则示例见第 9 节。
- 资源注册完成即可订阅，不等待 `Start()`。正则只匹配建立时的当前目录，不处理运行中新增、移除或同路径重注册；初始读取失败不自动重试。
- 原生资源须显式发布；`GetPathFor(nameof(资源成员))` 解析成员特性中的名称和 Thing 实例路径。分离的 Config 方法须在 Set 实现上标记 `[Cfet2Event]`。
- 远程 `SubscribeResource` 返回只确认初始 Get 与本地订阅注册，不等待远端成功 ACK；后续可能进入 Failed，须查询 WebSocket 诊断或日志。初始读取与注册之间不存在原子性保证。
- EventHub 关闭行为和配置边界以 [Core README](../../README.md) 为准。

| 升级项 | 当前状态 |
|---|---|
| Core `netstandard2.0`、测试 `.NET 10` | 已实现 |
| Hub 单资源订阅与普通事件包装 | 已实现 |
| 原生事件标记、标量及受支持容器自动观察 | 已实现 |
| 通信模块统一注册和路由 | 已实现 |
| Core 正则资源匹配、组合订阅、独立观察句柄 | 已实现 |
| 单次正则匹配 100ms 超时及分发隔离 | 已实现 |
| 源端样本时间戳与事件快照 | 已实现，保留既有传输能力 |
| WS 精确资源订阅 | 现有能力，本次保持兼容 |
| WS 正则订阅触发来源端观察 | 已接入，保留精确分支；异步准备、revision 幂等与有界容量已实现 |
| 观察 Get 慢读监测 | 已实现，默认 100ms Error，独立于正则匹配超时，不强制中止读取 |
| AutoObserveList / AutoDenyList、公共比较与主动变化发布、minDelta | 已实现，列表和阈值采用启动快照 |
| HTTP 事件订阅传输选择、WebSocketPortOffset | 已实现；事件协议冲突在注入与注册表写入前拒绝，失败不留下部分登记 |
| MQTT 实际通信与观察租约 | 未实现，标准宿主拒绝选择 MQTT |

详细设计与实施记录：[Core 正则资源观察实现文档](<../../Code/CFET2Core/doc/Core 正则资源观察/Core 正则资源观察实现文档.md>)；接入文档：[Core 正则资源观察接入 WS 文档](<../../Code/CFET2Core/doc/Core 正则资源观察/Core 正则资源观察接入WS文档.md>)。

CFETCore 是本次 CFET 升级中的主要改动部分。整体目标是在保持 Core 框架兼容性的基础上，升级通信、事件和订阅模型，并统一资源访问、事件订阅和远程通讯的管理方式。

本次 CFET 升级后不再支持 .NET Framework 4.6.1，CFET 项目整体升级到 `net10.0`。但 CFETCore 仍然保持 `netstandard2.0`，因为 Thing 仍然是 `netstandard2.0`。

## 升级目标

1. 保持 CFETCore 项目的 target framework 为 `netstandard2.0`。
2. 新增资源动作：`Subscribe`。
3. 重做事件设计，支持资源变化事件。
4. 统一远程通讯管理，由 `CommunicationManager` 管理资源访问和事件订阅。
5. 升级 `CommunicationModule` 设计，补充事件订阅通信接口。
6. 新增 HTTP 和 WebSocket 两类通信模块职责划分。
7. 为本地业务、WS 和后续 MQTT 提供统一资源匹配与单资源观察句柄，避免各模块重复轮询。
8. 区分事件过滤和实际观察资源集合，保留主动事件的完整匹配范围。

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

已实现接口：

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

这里的 `SubscribeResource` 语义面向 Status 和 Config 资源，Method 不支持该资源级接口，也不进入自动观察。普通事件过滤订阅仍可接收任意匹配来源的主动事件，不能将此限制理解为 Method 路径的主动 Publish 一律被禁止。

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

已实现的 Core 能力：

1. 标识资源是否支持原生事件。
2. 为不支持原生事件的资源建立内部观测机制。
3. 对观测到的资源变化统一调用 `EventHub.Publish`。
4. 事件过滤和订阅逻辑继续由 `EventHub` 负责。
5. Method 资源不进入自动观察；普通事件过滤与资源类型限制分开。
6. 资源级订阅返回初始值和后续事件 Token。
7. 单资源订阅先获取初始值，再注册事件；两步并非原子操作，也不保证回调一定在调用方处理 InitialValue 之后运行。
8. 单资源初始值获取失败时整体失败；新增正则组合订阅采用逐资源结果，单资源失败不撤销其他观察和原始事件订阅。

当前测试覆盖：

1. `[Cfet2Event]` 资源只接收 Thing 主动发布，不被 Core 内部观测。
2. 未标记 `[Cfet2Event]` 的 Status/Config 可以被 Core 周期观测，变化后发布 `changed`。
3. 多个订阅者订阅同一未标记资源时只建立一份观测。
4. 初始 `Get` 失败时抛错，并且不保留订阅或观测状态。
5. WebSocketCM 服务端当前将精确命中本地资源的来源条件交给 `ResourceSubscription`；正则等其他来源条件使用 `SubscribeResources` 组合入口，建立原始事件过滤并尝试观察匹配资源。精确命中但不可资源订阅的 Method 或非法事件类型会拒绝订阅。

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

远程通讯统一管理已经通过资源通信和事件通信两类模块实现。

原有资源访问链路：

```text
Hub -> CommunicationManager -> CommunicationModule
```

新增事件订阅链路：

```text
EventHub -> CommunicationManager -> IEventCommunicationModule
```

已提供事件通信抽象（虽然名称以 I 开头，当前声明实际为抽象类）：

```text
IEventCommunicationModule
```

该接口用于表达通信模块支持远程事件订阅能力。这样 `CommunicationManager` 可以通过接口区分：

1. 负责资源访问的 `CommunicationModule`。
2. 负责事件订阅的 `IEventCommunicationModule`。

其当前成员包括 `ProtocolNames`、`Start()`、`Dispose()`、`GetFullHost(...)`、`Subscribe(Token, EventFilter)`、`UnSubScribe(Token)` 和 `RegisterEventCallback(...)`。本次不修改这些通信协议接口。

## 6. 新的通信模块职责

新增两个通信模块实现：

```text
HTTPCM
WebSocketCM
```

职责划分：

1. `HTTPCM` 负责远程资源访问。
2. `WebSocketCM` 负责远程事件订阅。

现有 `WebSocketCM` 已采用远程端点连接复用：

```text
旧设计:
  一个事件订阅对应一个 WebSocket 连接。

新设计:
  同一远程端点的多个订阅共用一个 WebSocket 连接。
```

这样可以减少连接数量，并让远程事件订阅由 `CommunicationManager` 统一维护。

Core 公共正则观察能力已接入 WS 的 `SubscribeLocal` 非精确来源分支；未扩展 WS 观察状态 ACK、查询或推送协议。远端 Core 组合入口沿用现有事件路由，不在接收端匹配本地目录，且不返回来源端逐资源观察结果。

## 7. Core 正则资源观察与共享句柄（2026-10-09）

### 7.1 新增公共 API

```csharp
IReadOnlyList<ResourceBase> MatchLocalResources(EventFilter filter)
ResourceObservationResult AcquireResourceObservation(ResourceBase resource)
ResourceGroupSubscription SubscribeResources(EventFilter filter, Action<EventArg> handler)
```

| 接口 | 行为 |
|---|---|
| `MatchLocalResources` | 用原始条件匹配当前注册资源的 `(Path, "changed")`；不调用 getter、不建立观察，仅支持本地 Host |
| `AcquireResourceObservation` | 为一个已注册资源获取独立句柄，统一检查观察资格；不创建事件订阅 |
| `SubscribeResources` | 保存原始事件过滤器，并为匹配且符合条件的本地资源获取一组观察句柄；远端 Host 仅走已有事件路由 |

`ResourceObservationResult` 提供 `ResourcePath`、`Status`、`Reason` 和 `Handle`。状态分别为 `Started`、`Shared`、`Skipped`、`Failed`；失败或跳过时没有句柄。

`ResourceGroupSubscription` 提供事件 `Token`、逐资源 `ObservationResults`、`HasLocalObservationResults` 和 `Dispose()`。它没有单一的 `InitialValue`：初始有效读取用于观察基准，不作为 changed 发布。远端调用的 `HasLocalObservationResults` 为 false、结果列表为空，不能据此判断来源端观察成功。

```csharp
using var subscription = MyHub.SubscribeResources(
    new EventFilter("/device/.*", "changed"),
    e => Console.WriteLine($"{e.Source}: {e.Sample.ObjectVal}"));

foreach (var result in subscription.ObservationResults)
{
    Console.WriteLine($"{result.ResourcePath}: {result.Status}, {result.Reason}");
}
```

### 7.2 匹配、初值及失败规则

- 来源和事件类型保留忽略大小写、完整匹配语义；多条条件之间为或，同一条条件的来源与事件类型必须同时命中。
- 自动观察仅针对匹配 `changed` 的条件；只订阅 alarm 不启动轮询，不能靠轮询替代业务事件。
- 原生事件、不支持的类型和所有含参数 getter 的资源不自动观察；原始事件过滤器仍有效。
- 正则只在订阅建立时匹配一次。注册后即可尝试读取，不等待 Start，不补扫后续注册资源。
- 初次读取失败返回该资源的 Failed，不建立其观察引用、不后台重试；其他资源和事件订阅继续。全部没有观察句柄也可以保留有效事件订阅。
- 已建立观察后的临时读取失败继续沿用下一周期读取；首次成功建立观察时只保存基准，后续有效值通过独立快照进行标量或容器内容比较；数值标量使用配置的 minDelta 与上次成功入队的基准比较。
- 默认观察周期为 500ms，通过 `GlobalConfig.ResourceObservationIntervalMs` 配置。已支持 ResourceMinDelta / ResourceMinDeltaOverrides，默认阈值 0；容器保持内容比较，浮点边界不加 epsilon，也不保证捕获两次采样之间的每次变化。

### 7.3 生命周期与并发

事件 `Token` 与资源 `ObservationHandle` 分开。相同资源由多个句柄持有，底层只有一份有效轮询；重复条件在同一个组合订阅中只获得一个资源句柄。新需求加入不会重置原有比较基准。

组合对象 Dispose、直接释放其 Token 或通过 Hub 退订，都会释放自身句柄组；独立获取的句柄由调用方释放。最后一个有效句柄退出后停止观察，不影响其他资源。句柄释放幂等。

设备 getter 在管理器全局锁外执行；按资源协调读取、取消和重新获取，迟到结果不能由已经失效的任务继续发布。关闭拒绝新增观察并撤销现有引用，`DisposeThings()` 在释放设备前停止观察，但无法强制中断已进入的同步 getter。

普通 `Subscribe` 仍然仅登记事件过滤器，不隐式读取资源；旧 `SubscribeResource` 的单资源初值与失败契约保持不变。

### 7.4 正则计算超时

`EventFilter.Matches(source, eventType)` 供事件过滤与资源发现复用。来源、事件类型的每次 regex 匹配设置 100ms 超时，这不是整个订阅总时限，也不是订阅有效期。

- 初始匹配超时或表达式无效：拒绝本次新组合订阅，清理新增状态。
- 事件分发匹配超时：记录限频诊断，跳过该订阅对本次事件的匹配，继续处理其他订阅，不自动退订。
- 添加非法条件先校验再提交，不破坏已有 EventFilter 条件。

## 8. ISample 源端时间戳

保留已有 `DateTimeOffset? Timestamp`，时间统一为 UTC。开发者显式提供的源端时间优先；主动 Publish 缺少时间时采用发布调用时间，自动观察缺少时间时采用检测时间。普通 Get 与远端事件接收不补造时间，旧消息缺失时为 null。

本地 Publish 为事件和样本 Context 建立快照，不修改调用者的时间戳。现有 WS 完整/精简模式以及 HTTP JSON、MessagePack 时间传输行为继续保留；本次没有重做通信序列化，也未实现 MQTT 时间传输。

## 9. 当前实现缺陷与使用注意事项

### 9.1 参数化初读与主动事件已支持，参数化自动观察仍不支持

**普通 Get 和精确 SubscribeResource 初读可按地址传入参数，但所有含参数 getter 都不进入自动观察。** 后续变化由业务主动发布，精确订阅按调用者提交的完整路径接收。公共 PublishResourceIfChanged 已支持参数路径及独立通道比较基准；这不表示参数化轮询已经实现。无参数观察仍以基础资源路径共享任务。

例如：

```csharp
[Cfet2Status]
public double Temperature(int channel)
```

注册目录只有 `/device/Temperature`，不会自动注册 `/device/Temperature/1`、`/device/Temperature/2`。以下假设 channel 必填、事件类型订阅为 changed：

| 调用或来源表达式 | 当前结果 |
|---|---|
| 普通 `Get("/device/Temperature/1")` | 可以按普通资源访问规则传入 channel；是否成功取决于资源实现 |
| 本地 `SubscribeResource("/device/Temperature/1", handler)` | 按 channel=1 读取一次初值，精确接收该完整路径的主动事件；不建立自动观察 |
| `SubscribeResources(new EventFilter("/device/Temperature", "changed"), handler)` | 匹配到资源，但含参数 getter 检查使结果为 Skipped，原因 `Parameterized getters require active publication and do not support automatic observation.`，没有观察句柄；原始事件订阅保留 |
| `SubscribeResources(new EventFilter("/device/Temperature/.*", "changed"), handler)` | 不匹配基础注册路径；此例 ObservationResults 为空，不是 Skipped；事件订阅成功，但不会枚举 channel，也不启动自动观察 |

对于 `/device/Temperature/.*`，业务主动发布 `/device/Temperature/1`、`/device/Temperature/2` 的 changed 仍可被接收；基础路径 `/device/Temperature` 则不匹配该表达式。事件接收成功不代表参数化读取已被观察。

即使 getter 所有参数都有默认值，各自动观察入口仍返回 Skipped，不调用 getter；精确 SubscribeResource 可读取默认参数初值，但事件条件保留调用者提交的路径，不自动改写成默认参数详细路径。`/*` 也不是子路径通配符：正则中的 `*` 修饰前面的 `/`，例如 `/device/Temperature/*` 不能匹配 `/device/Temperature/1`。

远端 `SubscribeResource` 初始 Get 即使带参数成功，也不能证明来源端已建立对应参数的观察。本限制影响自动观察，不是禁止普通带参数 Get 或主动事件。

后续若支持参数化观察，需要确定参数来源/枚举范围、按“资源 + 参数”共享轮询和比较基准，以及事件来源路径如何表示参数；本次未实现。

### 9.2 初始失败的实际错误详情丢失

Code review 已复现：标准资源读取把 getter 异常放入无效样本的 `ErrorMessages`，当前观察入口却只返回 `Initial read returned an invalid sample.`。因此组合结果和来源端日志丢失实际设备错误。后续需保留样本错误详情并增加断言；该问题尚未修复。

### 9.3 订阅成功与观察成功的区别

- 正则组合订阅可以在零匹配、全部跳过或部分读取失败时成功保留事件订阅；应检查本地 ObservationResults。
- 来源端逐资源结果是建立时快照，不是实时健康状态。WS 本阶段不新增观察状态 ACK、查询或推送。
- 仅凭长时间没有 changed 无法区分值没有变化和读取失败。
- 初始失败不自动重试；实际重新建立 Core 组合订阅才会重新尝试，WS 后续聚合接入中复用已有组或重复 revision 不等于重新读取。
- 固定目录和无参数化观察是当前边界，不应通过扩大 regex 推断已经支持动态资源或所有参数组合。

## 10. 本次验证记录与后续范围

2026-10-09 Core 实现验证：新增 `RegexResourceObservationTests` 19 项；与关闭测试合计 27 项通过；最终 Core 全量 174 项通过，0 失败、0 跳过。review 时再次运行的 27 项相关测试也通过。测试通过不覆盖第 9 节尚未修复的错误详情问题，也不代表支持参数化自动观察。

覆盖正则独立启动、重叠共享、Token 释放、部分失败不重试、原生及主动事件、注册后不等待 Start、匹配快照、正则超时、远端路由、并发获取、初始化中退订/关闭和运行期读取恢复。测试项目为 `Code/CodeTest/CodeTest.csproj`，Core 保持 `netstandard2.0`，测试运行于 .NET 10。

后续 WS 接入已完成：首次接入验证为 WS 全量 222、Core 全量 185 项通过，均为 0 失败、0 跳过。新增测试覆盖慢 Get、异步准备、revision 失败幂等、容量拒绝、ACK 重发/重连、客户端 Hub 回调及释放。实际配置、后续验证批次和限制见仓库内 [WS 接入文档第 13 节](<../../Code/CFET2Core/doc/Core 正则资源观察/Core 正则资源观察接入WS文档.md#13-实际实现与验证摘要>)。

自动观察列表已实现：AutoObserveList 在全部组件 Start 成功后持有常驻观察，AutoDenyList 在统一观察入口禁止轮询，两者均不屏蔽主动事件。配置采用启动快照，修改后重启生效。该阶段全量回归 Core 218 项、WS 243 项通过；后续移除配置锁的专项回归 Core 23 项通过。配置、生命周期和测试范围见 [自动观察列表实现方案](../../Code/CFET2Core/doc/自动观察列表/自动观察列表实现方案.md)。minDelta 和宿主通信选择配置已在后续阶段实现，见 [minDelta 使用示例](../../Code/CFET2Core/doc/主动变化发布/minDelta使用示例.md) 和 [通信配置与路由](../../Code/CFET2Core/doc/通信配置与路由/通信配置与HTTP事件订阅路由实现方案.md)。MQTT 实际通信及资源租约仍属于后续范围。

慢 Get 配置为 `ResourceSlowGetThresholdMs`，默认 100ms，执行期间可报警，返回后补耗时；不强制中断、不改变有效结果。WS 准备默认执行并发 4、排队容量 256，满载拒绝且不自动重试。已复现不同重叠 regex 的既有重复回调问题，按已确认边界独立记录，未在本次加入跨聚合去重。

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
10. Core 正则观察公共能力已实现：资源匹配、组合订阅、共享句柄、失败隔离和正则超时；WS 接入及慢 Get 配套监测也已完成，实际记录见独立文档。

## 已落定规则与尚待实施事项

已落定：注册后即可订阅，不等待 Start；正则建立时匹配一次；初次失败不自动重试；单资源失败不撤销整个组合订阅；单次正则匹配 100ms；不扩展 WS 观察状态协议。观察周期、相等性比较及事件通信抽象已经有实现，不再列为未确定事项。

尚待实施：第 9 节错误详情修复、重叠正则既有重复路由的独立设计、MQTT 实际通信及资源租约。事件协议冲突注册契约已修正，注册前检查已有协议和模块内重复，http 别名允许共享。AutoObserveList / AutoDenyList、容器比较、PublishResourceIfChanged、minDelta 和宿主通信选择配置已实现，不再列入后续范围。带参数 Get 的自动观察当前不支持，若要补齐，需先细化参数实例与事件来源设计。

# Core 正则资源观察实现文档

日期：2026-10-09  
状态：Core 实现已落地；实际接口和验证记录见第 12 节。WS 接入及慢 Get 配套监测已完成，最新验证见文末第 14 节。前文保留设计说明，以第 12 节记录的实际接口为准。

配套文档：[Core 正则资源观察接入WS文档](Core%20正则资源观察接入WS文档.md)。

2026-10-10 后续扩展：自动观察增加数组、List、Dictionary 的独立快照与内容比较，见 [自动观察引用类型变化实现方案](../自动观察引用类型/自动观察引用类型变化实现方案.md)。本文此前的类型范围及测试数量属于对应阶段记录。

## 1. 目标和范围

为本地业务、WS 以及后续 MQTT 提供统一的资源匹配和共享观察能力。正则订阅能够从当前已注册的资源目录发现资源，不依赖这些资源已经发布过事件，也不依赖已有精确订阅启动轮询。保持现有行为，不新增等待 Start 的机制；实际可读性由读取结果判断。

本阶段范围：

- 原始来源正则和事件类型匹配。
- 单资源观察句柄及共享轮询。
- 本地正则组合订阅、精确订阅兼容及可靠释放。
- 逐资源处理结果、失败清理和必要的并发控制。

已确定的简化边界：资源目录固定，不处理运行中资源新增、移除和同路径重注册。不增加动态目录监听、注册代次或热插拔管理；订阅建立时对当前已注册资源匹配一次，不在 Start 完成后重新扫描。

本阶段的正则观察基础不包含 MQTT、自动观察列表、minDelta 或通信配置。自动观察列表已在后续阶段实现，见 [自动观察列表实现方案](../自动观察列表/自动观察列表实现方案.md)；其余能力仍属于后续范围。

## 2. 现有代码与缺口

| 文件 | 现有行为 | 本阶段调整 |
|---|---|---|
| `newcfet2core/Code/CFET2Core/Hub.cs` | `SubscribeResource` 检查资源、读取初值、订阅事件并启动单资源观察 | 提供公共匹配、观察及组合订阅入口，保留旧接口契约 |
| `newcfet2core/Code/CFET2Core/Event/EventFilter.cs` | `Predicate(EventArg)` 对来源和事件类型执行完整匹配，忽略大小写 | 提取可复用匹配方法，增加每次匹配 100ms 超时保护 |
| `newcfet2core/Code/CFET2Core/Event/ResourceObservationManager.cs` | 按路径共享轮询，`tokenToResourcePath` 限制一个事件 Token 对应一个资源 | 将观察引用与事件 Token 解耦 |
| `newcfet2core/Code/CFET2Core/Event/EventHub.cs` | 管理事件 Token，退订时调用观察释放 | 协调事件退订与组合观察引用清理 |
| `newcfet2core/Code/CFET2Core/Event/ResourceObservationTypePolicy.cs` | 限制自动观察支持的声明类型 | 复用，不因正则命中放宽限制 |
| `newcfet2core/Code/CFET2Core/Hub.Modules.Partial.cs` | 注册 Thing、调用 Start、释放 Thing | 保持注册后可订阅，不增加 Start 门槛；核对关闭时的观察停止顺序 |

上述现状依据本次方案讨论前的源码阅读；实施时应核对工作区最新代码。

## 3. 核心概念

### 3.1 事件 Token 与观察句柄

- `Token`：沿用现有事件订阅类型，负责事件接收关系及退订。
- `ObservationHandle`：新增单资源观察使用凭证，表示一个需求持有某个资源的观察引用；可幂等释放。
- 组合订阅：一个事件 Token，加上一组实际获取成功的观察句柄，以及处理结果。

一个正则事件订阅可能持有多个观察句柄。同一个资源可以有多个句柄，但 Core 只维护一份轮询。

### 3.2 公共共享观察表

逻辑结构：

```text
资源路径 → ResourceObservation
             Resource
             有效 HandleId 集合
             比较基准
             轮询任务
             取消控制及启动/停止状态
```

例如 WS 和后续 MQTT 都需要温度：

```text
温度 → { 本地 H1, WS H2, MQTT H3 } → 唯一轮询任务
```

释放 H3 只撤销 MQTT 自身需求。H1 或 H2 仍有效时，观察继续。最后一个句柄释放，Core 才停止观察。

Core 不需要知道句柄来自 Session、regex 订阅还是 MQTT 租约，也不负责 MQTT 的到期计时。

## 4. 公共能力与接口草案

以下为概念接口，具体签名、返回类型和同步/异步形式在编码前细化，并兼容 Core 当前 `netstandard2.0` 目标。

```csharp
MatchLocalResources(EventFilter filter)
AcquireResourceObservation(resource)
SubscribeResources(EventFilter filter, handler)
```

### 4.1 匹配资源

`MatchLocalResources` 只返回本地当前已注册、满足来源及 `changed` 事件条件的资源快照，不检查 Start 是否完成，不调用 getter，不获取观察引用。

匹配结果与自动观察资格分开：匹配到 Method、原生事件资源或不支持类型时，由获取阶段返回跳过原因。后续 MQTT 可以独立使用匹配结果按资源续租。

使用注册资源的完整 `Resource.Path`。不从历史事件发现资源，不用 MQTT Topic Filter 反推业务 regex，不对 regex 做路径字符串式的改写。

### 4.2 单资源获取

`AcquireResourceObservation` 在公共入口统一检查资格，并返回句柄或明确结果：

- 已启动观察。
- 已共享现有观察。
- 跳过：原生事件、非 Status/Config、不支持类型、不能按观察方式读取等。
- 失败：初始读取抛错或样本无效等。

资格检查必须在获取入口执行，不能只依赖调用方事先筛选。后续 BlackList 也应在此统一落实。

### 4.3 组合订阅

`SubscribeResources` 为本地业务和 WS 提供薄封装，持有：

```text
原始条件快照
事件 Token
资源路径 → ObservationHandle
逐资源处理结果
```

流程为：校验条件并匹配资源快照，登记原始事件过滤器，获取匹配资源的观察句柄，再返回组合对象。重复命中的资源只获取一次。整体失败时清理已经登记的事件订阅和句柄。

远端 Host 沿用现有通信路由，资源匹配和观察在来源端完成，不能在接收端匹配本地资源。本阶段不扩展 WS 观察状态协议；逐资源结果仅保留在来源端 Core 返回对象和诊断中，远端返回结果不得伪装成来源端观察全部成功。

## 5. 正则语义及事件边界

提取类似 `Matches(string source, string eventType)` 的公共判断方法：

```csharp
// 已产生事件的分发
Matches(eventArg.Source, eventArg.EventType)

// 尚未产生事件时，判断资源是否需要自动观察
Matches(resource.Path, "changed")
```

保留现有语义：忽略大小写、完整匹配；多条来源/事件类型条件之间为或，且必须同一条条件同时匹配来源与事件类型。完整匹配行为沿用现有实现，不擅自改成子串匹配或改写正则锚点。

| 条件或资源情况 | 自动观察 | 事件接收 |
|---|---|---|
| `/device/.*` + `changed` | 对符合条件资源启动或共享 | 使用原始规则 |
| `/device/.*` + `.*` | 因包含 `changed`，允许观察 | 仍可接收其他匹配事件 |
| `/device/.*` + `alarm` | 不启动 | 仍接收主动 alarm |
| 原生事件资源 | 不额外轮询 | 正常接收原生事件 |
| 不支持观察的资源类型 | 跳过 | 主动事件不受影响 |
| 路径未登记但主动发布事件 | 不属于观察集合 | 原始规则匹配时仍可接收 |

观察资源集合绝不能替代事件过滤范围。组合订阅即使没有任何观察句柄，仍可具有有效的事件订阅。

### 5.1 正则超时的处理范围

正则匹配超时是限制一次 regex 计算时间，防止复杂表达式回溯耗时过长；它与订阅有效期、MQTT 租约和等待事件无关。

已确认本阶段增加正则匹配超时保护，来源和事件类型的每次 regex 匹配均采用 100ms。该值不代表整个资源扫描或整个订阅建立过程的总时限。

- 订阅建立时的初始匹配超时：拒绝本次新订阅，回滚已创建的订阅和观察状态，不影响其他有效需求。
- 事件分发时匹配超时：捕获异常、记录限频诊断，跳过该订阅对本次事件的匹配，继续处理其他订阅；不自动退订。
- 表达式语法无效：建立时明确失败，不留下部分登记状态。

## 6. 自动观察与共享规则

只有已注册的 Status、Config，未声明原生事件，且通过现有类型策略并可无参取得有效初值时，才新建自动观察。不以 Start 是否完成作为额外门槛，不可因正则命中绕过类型和读取条件。

第一个有效需求读取初始样本，建立基准，再启动观察。后续句柄共享现有任务，不重置比较基准，也不重复创建任务。初值不作为 changed 发布。

现有精确订阅仍需返回自己的初值；即使共享轮询，不能因此删除旧接口要求的初值读取。这个读取不应覆盖共享观察已有的比较基准。

轮询保留现有变化判断、间隔和时间戳行为：有效样本变化时发布 changed，保留源端时间；缺少时间时沿用检测时间补齐。不新增 minDelta。

## 7. 兼容与释放

- 普通 `Subscribe` 继续只登记事件过滤器，不隐式启动资源读取。
- `SubscribeResource` 保留单资源类型限制、初值返回及失败契约，内部改用统一句柄。
- `ResourceSubscription.Dispose()`、返回 Token 的退订，以及组合订阅 Dispose，必须汇入同一套幂等清理逻辑，避免用户只退订 Token 时遗留观察。
- 组合订阅注销事件后，释放自己实际取得的全部句柄；一个释放失败不能阻断其余句柄清理，应保留诊断。
- Core 关闭时拒绝新增观察，停止现有任务；设备释放顺序应避免观察继续读取已释放设备。

并发实现要求：初始化读取在锁外执行，但通过按资源的启动占位或等效协调避免重复初始化/轮询；提交结果时再次检查需求和关闭状态。最后一次释放与重新获取交错时，协调旧任务退出与新任务启动，不能只用“该路径仍存在”判断旧任务是否有效。

同步 getter 可能无法强制取消；取消应阻止后续读取和迟到结果发布，不能宣称已经中止正在执行的设备 getter。

## 8. 固定目录、订阅时机和失败策略

已确认保持现有行为：资源注册完成后即可订阅，不新增统一等待 `StartThings()` 或单个 `Thing.Start()` 的机制。实际可读性由读取结果判断；不建立“等待就绪”需求队列。

正则在订阅建立时对当前注册目录匹配一次。业务若需覆盖全部目标资源，应在这些资源注册完成后建立订阅；此前尚未注册的资源不会在 Start 完成后自动补入。不增加周期目录扫描。

| 失败情况 | 已确定处理 |
|---|---|
| 单个资源不支持自动观察 | 跳过并报告，其余资源继续 |
| 单个资源初始读取失败 | 记录失败，不建立该资源的观察引用、不自动重试；其他资源和原始事件订阅继续有效 |
| 已观察资源某次读取失败 | 保留需求，下个轮询周期继续 |
| 零资源匹配 | 返回空观察组，保留事件过滤器 |
| 表达式无效、初始正则匹配超时或整体建立失败 | 拒绝新组合订阅，回滚新增状态 |
| 退订与初始化并发 | 不允许迟到初始化留下句柄 |

已确认正则组合订阅也不增加初始读取失败自动重试：不保留等待重试的资源需求，不增加重试任务。调用方需要重新建立实际的 Core 组合订阅才会重新尝试。WS 相同 revision 重发或复用已有聚合项不会重新读取，见 WS 接入文档。

已成功建立观察后的读取失败沿用现有行为：无效样本跳过本轮，普通读取异常记录诊断，下个轮询周期继续。初次失败和运行期失败的处理不能混淆。现有精确订阅仍在初值失败时抛出异常，不返回成功订阅对象。

## 9. 后续 MQTT 和 WhiteList 的复用边界

MQTT 保存“资源路径 → 一个观察句柄 + 最后续租时间”。原始 regex 每次作为 Core 匹配输入；已经持有的资源只续期，不增加句柄。超时只释放 MQTT 的句柄，不影响本地或 WS。

WhiteList 后续独立持有常驻句柄。BlackList 后续在公共获取入口阻止自动观察，不阻止事件过滤、主动 Publish 或普通读写。本阶段不实现这些管理器和策略。

## 10. 实施顺序与验收

1. 抽取公共匹配方法并加入 100ms 超时，验证正常匹配语义与现有 EventFilter 一致。
2. 引入单资源句柄，改造观察管理器并保持精确订阅兼容。
3. 提供当前注册资源快照和组合订阅，落实部分失败结果和初次失败不重试规则。
4. 完成释放、异常和并发验证，再交给 WS 接入。

验收至少包含：

- 仅有本地正则订阅即可启动观察并收到变化。
- 多条条件重复命中同一资源时，一个组合订阅只持有一个句柄。
- 精确订阅和重叠正则共享唯一轮询，逐个释放不误停。
- alarm 条件不触发轮询，原生事件和主动 Publish 正常接收。
- 不支持类型、无参读取失败、无效初值均有明确结果且不泄漏。
- 资源注册后、Start 调用前可以订阅；正则只匹配建立时目录，不等待或在 Start 后补扫。
- 初始读取失败不触发后台重试，成功资源和原始事件过滤器保留；运行期读取失败下个周期继续。
- 初始正则匹配超时拒绝新订阅并清理；分发超时不影响其他订阅或终止分发循环。
- 退订 Token、Dispose 组合对象、重复释放和应用关闭均正确清理。
- 并发获取、最后释放后立即重订阅、读取中关闭无重复任务或迟到发布。
- 原有初值、类型限制、时间戳及精确订阅测试继续通过。

## 11. 已确认决策与实现细化

2026-10-09 已确认：注册后即可订阅，不等待 Start；正则建立时匹配一次；初始读取失败不自动重试；部分资源失败不拒绝整个正则订阅；正则每次匹配超时为 100ms；不扩展 WS 观察状态协议。

公共 API 最终签名、结果类型及内部同步方式按这些已确认行为细化，不再将上述行为列为待确认项。实际实施情况见下一节。

## 12. Core 实施记录（2026-10-09）

### 12.1 已实现接口

实现位于 `newcfet2core/Code/CFET2Core/Hub.Observation.Partial.cs`：

```csharp
IReadOnlyList<ResourceBase> MatchLocalResources(EventFilter filter)
ResourceObservationResult AcquireResourceObservation(ResourceBase resource)
ResourceGroupSubscription SubscribeResources(EventFilter filter, Action<EventArg> handler)
```

- `MatchLocalResources`：只匹配本地目录；拒绝远端 Host，不调用 getter。
- `AcquireResourceObservation`：只接受当前注册目录中的资源对象；返回 Started、Shared、Skipped 或 Failed，以及路径、原因和成功时的 Handle。调用方负责释放独立获取的 Handle。
- `ResourceGroupSubscription`：提供 Token、ObservationResults、HasLocalObservationResults 和 Dispose。逐资源结果是建立时快照，不是运行健康状态。
- `ObservationHandle`：提供 Id、ResourcePath、IsReleased 和幂等 Dispose。
- `EventFilter.Matches`：供事件和资源发现复用；来源与事件类型的单次正则匹配超时为 100ms。新增条件先校验后提交，非法条件不破坏原有过滤器。

本地调用示例：

```csharp
var subscription = hub.SubscribeResources(
    new EventFilter("/device/.*", "changed"),
    eventArg => HandleChanged(eventArg));

foreach (var result in subscription.ObservationResults)
{
    // 检查 ResourcePath、Status 和 Reason。
    // Skipped / Failed 不会触发后台初值重试。
}

// 两种方式均会注销事件并释放本订阅的全部观察句柄。
subscription.Dispose();
// 或 subscription.Token.Dispose();
```

独立观察示例（没有事件订阅副作用）：

```csharp
var resource = hub.GetLocalResouce("/device/Temperature");
var result = hub.AcquireResourceObservation(resource);
// 成功时保存 result.Handle，需要撤销自己的需求时释放。
result.Handle?.Dispose();
```

### 12.2 生命周期和兼容

- 单资源、组合订阅和独立句柄共用同一个 ResourceObservationManager；每个资源只维护一份有效轮询。
- 按资源保留读取锁，协调初始读取、旧任务退出和重新获取；getter 不持有管理器全局锁。迟到结果在发布前检查当前观察身份。
- 事件 Token 在开始组合读取前绑定句柄组，退订或关闭后到达的句柄会立即释放。
- `DisposeThings()` 先撤销全部观察，再调用设备 Dispose；不能强制终止已经运行的同步 getter。
- 现有 `SubscribeResource` 继续返回自己的有效初值，初次失败仍抛错；普通 `Subscribe` 不启动观察。
- 远端组合入口只使用现有事件通信路由，HasLocalObservationResults 为 false，结果列表为空，不能据此宣称来源端自动观察成功。WS 服务端自动观察接入仍需后续实施。
- 注册后即可订阅，不等待 Start；目录不自动重扫，初值失败不自动重试，已建立观察后的读取失败仍在下一周期继续。

### 12.3 测试覆盖

新增 `newcfet2core/Code/CodeTest/Event/RegexResourceObservationTests.cs`，共 19 项测试，覆盖匹配规则、注册后立即订阅、共享和独立释放、比较基准、部分失败不重试、原生及主动事件、目录与条件快照、正则超时、远端路由、并发获取、退订期间初始化、关闭期间批量初始化和运行期恢复。

原有 `EventHubDisposalTest` 中依赖观察管理器私有结构的测试已适配新的 slots 结构，继续验证关闭不等待阻塞 getter，以及 getter 返回后任务正常结束。

该阶段验证结果：新增测试与关闭测试共 27 项通过；当时 Core 全量测试 174 项全部通过（0 失败、0 跳过）。Core 保持 `netstandard2.0`，测试运行于 .NET 10。该阶段未进行 WS 接入或 WS 端到端功能验收。可在 Core 仓库根目录运行 `dotnet test Code/CodeTest/CodeTest.csproj` 复验当前版本；后续新增用例会改变测试数量。

## 13. 当前实现缺陷与注意事项

### 13.1 不支持带读取参数的自动观察

当前 `SubscribeResources` 只匹配注册资源路径，轮询通过无参 `resource.Get()` 读取。它不枚举参数，不从正则生成参数组合，也不按“资源 + 参数”分别管理观察和比较基准。

例如资源定义为：

```csharp
[Cfet2Status]
public double Temperature(int channel)
```

注册目录包含 `/device/Temperature`，不会自动为各个 channel 注册 `/device/Temperature/1`、`/device/Temperature/2`。以下均假设事件类型条件为 `changed`，且该资源的 channel 没有默认值：

| 来源正则 | 当前资源匹配及观察结果 | 主动事件接收 |
|---|---|---|
| `/device/Temperature` | 命中注册资源，但 getter 要求参数；结果为 `Skipped`，原因为 `The getter requires arguments.`，Handle 为 null | 接收该基础路径的 changed，不接收 `/1`、`/2` 路径 |
| `/device/Temperature/.*` | 不匹配基础注册路径，参数路径也未登记；该示例下 `ObservationResults.Count == 0`，没有观察句柄，整体事件订阅仍成功 | 可以接收 `/device/Temperature/1`、`/device/Temperature/2` 等来源的主动 changed |

因此，`/device/Temperature/.*` 当前只能过滤参数路径上的事件，不能触发带参数读取和变化检测。空观察结果不是 `Skipped`：前者没有匹配到注册资源，后者匹配到了资源但不符合自动观察条件。框架不会因为该事件订阅成功而自动轮询全部 channel。

其他注意事项：

- 正则 `/*` 不是子路径通配符：`*` 修饰前面的 `/`。例如 `/device/Temperature/*` 可以匹配基础路径及其后的零个或多个斜杠，但不能匹配 `/device/Temperature/1`；匹配任意子路径应使用 `/.*`。
- getter 所有参数都有默认值时，可以按无参方式调用并观察默认参数对应的值；这不代表支持枚举或指定参数观察。
- 现有本地 `SubscribeResource` 也没有完整的带参数观察支持：虽然路径查找可能定位到基础资源，但初始读取和后续轮询没有把路径参数传给 getter。
- 后续若支持此能力，需要明确参数来源和可枚举范围、按“资源 + 参数”共享观察的规则，以及事件来源路径如何表达参数。本阶段仅记录此限制，不扩展参数化观察。

### 13.2 初次读取失败的原始错误详情丢失（review 已确认，待修复）

标准 `ResourceStatus.Get` 会把 getter 异常转为无效样本，并把原始错误保存在 `ErrorMessages`。当前观察获取逻辑仅返回通用原因 `Initial read returned an invalid sample.`，没有保留这些详情，因此 Core 结果和来源端日志无法显示具体设备读取错误。

后续应在非空无效样本中提取 `ErrorMessages`，无详情时再使用通用提示，并补充验证错误详情保留的测试。该问题仅记录，尚未修复；前述通过的测试不覆盖错误详情保留。

## 14. WS 接入配套慢 Get 监测与最新验证

2026-10-09：来源端组合初读、观察轮询和本地精确订阅初读接入 `SlowResourceReadMonitor`。独立配置 `GlobalConfig.ResourceSlowGetThresholdMs` 默认 100ms；单调计时，共享监测器可在 Get 未返回时记录 Error，返回后记录最终耗时，按资源限频。报警不强制中断读取、不新增初读重试，不改变有效 Sample 与比较基线。

这与 EventFilter 单次正则计算的 100ms 超时无关。普通业务 Get 及远端网络 Get 未统一纳入此次监测。原第 13 节参数化观察和错误详情丢失限制仍成立。

该阶段新增慢读单元与真实读取集成测试合计 11 个结果；当时 Core 全量 185 项、WS 全量 222 项通过，均为 0 失败、0 跳过。后续验证批次、实际配置及既有重叠 regex 路由边界见 [WS 接入文档第 13 节](Core%20正则资源观察接入WS文档.md#13-实际实现与验证摘要)。自动观察列表阶段的最新验证见 [自动观察列表实现方案](../自动观察列表/自动观察列表实现方案.md)。

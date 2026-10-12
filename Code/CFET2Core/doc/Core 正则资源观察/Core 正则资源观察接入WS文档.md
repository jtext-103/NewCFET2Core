# Core 正则资源观察接入 WS 文档

日期：2026-10-09  
状态：WS 接入已实现并完成测试。前文保留接入设计；实际异步准备、revision、容量、慢 Get 监测及验证边界见本文第 13 节。

前置文档：[Core 正则资源观察实现文档](Core%20正则资源观察实现文档.md)。  

## 1. 目标与边界

使现有 WS 正则来源订阅能够在来源端启动匹配资源的 Core 自动观察，而不再只是等待已经有人发布的事件。

WS 不实现自己的 getter 轮询、变化判断或资源引用计数。原始 regex 仍用于事件过滤，Core 匹配出的具体资源只用于获取观察句柄。

本阶段假定资源目录固定，不支持运行中新增、移除和重注册。资源注册后即可订阅，不等待 Start；regex 建立时匹配当前注册目录一次，可读性由读取结果判断。初次读取失败不自动重试，观察资格及部分失败行为遵守配套 Core 文档。本阶段不扩展 WS 观察状态协议。

注意：Core 当前不支持带读取参数的自动观察。例如注册资源只有 `/device/Temperature`、getter 必须传入 channel 时，`/device/Temperature/.*` 不会匹配到该基础资源，也不会枚举 channel；观察结果为空，但原始事件订阅仍可接收 `/device/Temperature/1` 等路径主动发布的匹配事件。WS 接入不会自动补齐此能力。详细限制及与 `Skipped` 的区别见 [Core 实现文档第 13 节](Core%20正则资源观察实现文档.md#13-当前实现缺陷与注意事项)。

## 2. 当前服务端结构

以下为独立仓库 websocketcommunicationmodule 中的文件，相对该仓库的 `WebSocketCommunicationModule/` 目录列出，供说明接入职责；这些文件不属于 Core 仓库：

| 文件 | 职责 |
|---|---|
| `Server/Subscriptions/WebSocketSubscriptionManager.cs` | 远端订阅登记、按条件聚合、revision、事件分发和清理 |
| `Server/Subscriptions/ServerSubscriptionKey.cs` | 来源表达式和事件类型构成聚合键 |
| `Server/Subscriptions/AggregatedServerSubscription.cs` | 保存本地订阅、过滤器及远端订阅者集合 |
| `Server/Subscriptions/RemoteSubscriber.cs` | 远端会话、Token 和订阅状态 |
| `Server/WebSocketSession.cs` | 接收订阅/退订、发送 ACK、关闭时清理会话 |
| `Protocol/SubscribeAckMessage.cs` | 保留现有订阅结果消息，不新增观察状态字段 |

当前关系：

```text
远端订阅 (SessionId, RemoteTokenId)
    → 一个或多个 ServerSubscriptionKey(Source, EventType)
    → AggregatedServerSubscription
    → 一个本地 Core 订阅
```

`SubscribeLocal` 改造前对精确本地资源调用 `hub.SubscribeResource`，对其余来源条件只调用 `hub.Subscribe`。缺口在后者没有建立自动观察。

## 3. 接入后的聚合模型

继续使用现有聚合项，每项保存一个可释放的本地组合订阅：

```text
AggregatedServerSubscription
  原始 LocalFilter
  LocalToken：事件订阅 Token
  LocalSubscription：组合订阅 IDisposable
      事件 Token
      一组 ObservationHandle
      逐资源处理结果
  Subscribers：远端订阅者集合
```

`ObservationHandle` 是资源观察引用，不是事件 Token。WS 不为每个观察资源新增远端 Token；现有远端 Token 继续标识原始事件订阅。

相同聚合项的多个远端订阅者共享一组观察句柄。不同聚合项可以分别持有同一资源的句柄，最终由 Core 共享一份轮询。

## 4. 例子：重叠 regex 与释放

固定资源目录包含温度、压力：

```text
/device/temperature
/device/pressure
```

远端订阅：

```text
客户端 A：R = /device/.* + changed
客户端 B：R = /device/.* + changed
客户端 C：S = /device/temp.* + changed
```

服务端状态：

```text
聚合 R：订阅者 {A, B}
  事件 Token R
  温度句柄 W1、压力句柄 W2

聚合 S：订阅者 {C}
  事件 Token S
  温度句柄 W3

Core：
  温度 {W1, W3} → 一个轮询任务
  压力 {W2}     → 一个轮询任务
```

| 操作 | 服务端处理 | Core 结果 |
|---|---|---|
| A 退订 | R 仍有 B，保留 R | 都继续观察 |
| B 退订 | R 无订阅者，释放 R 的 Token、W1、W2 | 压力停止，温度仍由 W3 持有 |
| C 退订 | 释放 S 的 Token、W3 | 温度停止 |

一个连接可以有多个订阅，一个聚合项可以服务多个连接。WS 按会话发送消息与 Core 共享观察并不冲突。

## 5. 建立订阅流程

1. 接收原始来源/事件类型条件，保留现有协议和 PerformanceLevel 校验。
2. 执行现有 revision 检查、重复请求判断和条件去重。
3. 对已有聚合项复用其本地订阅，不重复获取观察句柄。
4. 对新聚合项构造来源端本地过滤器，确保 Host 为空，避免再次路由到远端。
5. 精确资源分支继续走现有 `SubscribeResource`，由改造后的 Core 使用统一单资源句柄。
6. 正则分支调用 Core 新组合入口 `SubscribeResources`，保存事件 Token 和组合对象。
7. 所有新项准备成功后，将远端订阅者加入聚合项并更新 remoteIndex。
8. 释放此次更新中不再需要的旧聚合关系，返回 ACK。

这里的准备成功指组合订阅成功建立，不要求每个匹配资源都观察成功。单资源读取失败不阻断其他资源，也不撤销原始事件订阅；失败结果留在来源端，不自动重试。

精确订阅契约保持现状；不能在未评估兼容性的情况下将其异常行为改成正则批量的“跳过并继续”。若后续希望统一精确路径事件类型表达式行为，应单独设计并测试。

继续使用现有精确路径识别逻辑，同时补充包含正则元字符的路径测试；资源观察集合不得改变原始事件过滤语义。

## 6. 事件分发保持原始过滤条件

Core 观察产生 changed 后，进入原有 EventHub 事件流程。聚合项的回调继续调用现有 FanOut 逻辑，按 Subscribers 向对应 Session 发送远端 TokenId 和事件载荷。

不能将原始 regex 替换为观察资源路径列表：

- 原生事件资源不轮询，但需要接收主动事件。
- 不支持自动观察的资源仍可主动 Publish。
- 未登记路径的主动事件只要匹配规则，也应正常转发。
- alarm 等自定义事件不能由轮询替代。

保留现有完整/精简传输模式及源端时间戳，不在接收端补造事件时间。本阶段不改变现有跨聚合项分发和客户端路由行为；增加重叠条件回归测试，防止接入观察后意外增加发送次数。

## 7. 退订、会话关闭和失败回滚

### 7.1 正常退订

`Unsubscribe(sessionId, remoteTokenId)` 删除该远端订阅在各聚合项中的关系。聚合项仍有订阅者时保留组合对象；没有订阅者时移除聚合项并调用 `LocalSubscription.Dispose()`。

组合对象负责注销 Core 事件 Token 和释放整组观察句柄。WS 不直接停止某个资源的轮询。

### 7.2 会话与模块关闭

沿用 `UnsubscribeSession` 清理该 Session 的全部远端订阅，复用正常退订流程。WS 模块 Dispose 释放所有剩余聚合项。

重复退订、显式关闭与会话 finally 清理同时发生，应保持幂等。异常清理至少记录诊断；不能静默吞掉释放异常后宣称引用已全部清理。

### 7.3 建立和更新失败

新 Core 组合订阅自身建立失败时，必须清理已经取得的 Token 和句柄。WS 外层继续回滚本次新建且未被使用的聚合项。

更新遵循“先准备新项，成功后切换，再释放旧项”。失败不能破坏已有有效订阅，也不能错误释放其他客户端共享的聚合项。

重复的同一 revision 请求返回之前的结果，不重新启动轮询。同条件仅更新 PerformanceLevel 时，也不能重新获取观察句柄。

## 8. 并发和锁范围

改造前服务端订阅流程在 `syncRoot` 中准备本地订阅。正则展开会涉及多个 getter，如果直接在该锁内逐个读取，可能长时间阻塞其他订阅和会话清理。

实施时需要评估并采用受控准备流程：锁内检查并预留待建立项，锁外调用 Core，锁内再次校验 disposed、revision 和项归属后提交。并发请求同一聚合项时应共享或等待同一准备结果，不能重复建组。

若准备完成前会话退出或模块关闭，应释放迟到的组合对象，不提交失效需求。外层锁只保护索引及状态，不承载设备读取和网络发送。

Core 负责资源任务并发安全；WS 仍需负责聚合项和远端关系的并发安全，两者不能互相替代。

## 9. 来源端观察结果与现有 ACK

已确认不扩展 WS 观察状态协议：保留现有 ACK 字段，不增加观察摘要、状态查询或状态变化推送，也不为此修改客户端消息解析。

逐资源建立结果保留在来源端 Core 返回对象和来源端诊断中，包括已启动/共享、跳过和初始读取失败的路径及原因。运行期读取错误沿用来源端诊断；建立结果不表示持续健康状态。

事件订阅成功不等于所有匹配资源观察成功。现有 ACK 的接受结果只表示事件订阅已接受，不能据此判断全部资源可读；远端仅凭长期没有 changed 也不能判断是值未变化还是读取失败。

| 情况 | 现有协议与来源端处理 |
|---|---|
| 合法规则，部分资源不能观察 | ACK 接受事件订阅；逐资源失败或跳过原因保留在来源端 |
| 原生事件或仅 alarm 条件 | 接受事件订阅，无需轮询 |
| 零匹配 | 接受事件订阅，来源端记录空观察集合 |
| regex 无效、初始匹配超过 100ms 或整体建立失败 | 通过现有拒绝结果和原因反馈，回滚新增状态 |
| 精确订阅初值读取失败 | 沿用既有精确订阅失败语义 |

初始 regex 匹配超时拒绝本次建立；事件分发时 regex 匹配超时则捕获并记录限频诊断，跳过对应订阅对本次事件的匹配，继续处理其他订阅，不自动退订。100ms 限制一次 regex 计算，不是整个订阅总时限。

正则组合订阅初始读取失败不自动重试。相同 revision 重发只返回缓存结果；更新 revision 但复用相同聚合项、或新增客户端加入已有聚合项，同样不重新获取失败资源。只有实际新建 Core 组合订阅时才会重新尝试；取消并重订阅若仍复用其他客户端持有的聚合项，也不保证重试。本阶段不新增强制刷新接口。

## 10. 与后续 MQTT 的区别

两者保留原始 regex，使用同一套 Core 匹配和单资源观察接口。

| 项目 | WS | 后续 MQTT |
|---|---|---|
| 事件分发 | 来源端按远端订阅关系发送 | Broker 分发 |
| 句柄归属 | 聚合订阅持有一组句柄 | 模块每个资源持有一个句柄 |
| 重叠 regex | 不同聚合项各持有句柄，Core 共享轮询 | 对已有资源只更新续租时间 |
| 释放 | 聚合项最后一个订阅者退出时释放整组 | 单资源超时后释放其句柄 |

MQTT 的资源级租约来自已确定需求，并非 Broker 强制要求。它与 WS 的句柄共同进入 Core 的同一资源持有集合；任何一方释放都不能影响其他有效持有者。

## 11. 修改清单及实施顺序

1. 完成配套 Core 文档中的共享句柄和组合入口，并通过本地验证。
2. 修改 `SubscribeLocal` 正则分支及聚合对象持有方式。
3. 完善准备/提交、revision 幂等、失败回滚与关闭竞争处理。
4. 补充来源端逐资源结果诊断和正则超时处理；保留现有 ACK 与客户端协议。
5. 执行服务端单元测试和真实 WS 端到端验证。

不改写通信模块自己的轮询，不增加客户端逐资源订阅消息，不要求客户端先查询资源列表。

## 12. 验收清单

- 没有任何精确订阅时，远端 regex 订阅独立启动来源端观察并收到 changed。
- 两个 Session 的相同条件共享一个聚合项及一组句柄。
- 重叠 regex、WS 精确订阅和本地订阅命中同一资源时，仅一份轮询。
- 一个订阅者退出不影响同组其他订阅者，最后退出时释放该组句柄。
- WS 释放后，本地仍持有的观察继续；最后一个 Core 句柄退出后停止。
- 原生事件、alarm、无法观察资源和未登记路径的主动事件按原始规则转发。
- 部分 getter 失败、不支持类型和零匹配的结果保留在来源端，事件订阅接受且不泄漏。
- 注册后、Start 前可以建立正则订阅；不等待 Start、不在其完成后补扫资源。
- 初始读取失败无后台重试；复用聚合项不重新读取失败资源，实际新建 Core 订阅才重新尝试。
- 初始正则匹配超时使用现有拒绝结果反馈，分发超时不影响其他订阅。
- 无效规则及中途建立失败正确回滚，旧 revision 对应订阅保持有效。
- 重复请求、仅 PerformanceLevel 更新不重复获取句柄。
- 订阅准备与退订、会话关闭、模块 Dispose 并发时不遗留迟到句柄。
- 精确订阅原有行为、完整/精简模式和源端时间戳保持兼容。
- ACK 不增加观察字段，现有客户端兼容；重复 revision 返回一致结果，不将接受状态当作全部资源观察成功。

首次接入阶段的执行结果为 WS 全量 222、Core 全量 185 项通过，0 失败、0 跳过；后续批次及实际实现边界见第 13 节。

## 13. 实际实现与验证摘要

### 13.1 准备与提交

非精确来源通过 `SubscribeResources` 建立组合订阅；精确已注册资源仍使用 `SubscribeResource`。两者的初读在服务端有界后台准备任务中执行，设备读取和网络发送不占订阅管理状态锁。

服务端分别维护准备状态、最后成功提交的订阅关系和最高请求版本。同组请求共享准备，同 revision 重发复用原结果；新版本可以转移仍需要的准备引用。更新准备失败保留最后成功提交的旧订阅，不恢复已被更高版本淘汰的旧准备。

退订、会话关闭或更新覆盖会使在途请求失效，迟到的 Core 订阅对象必须释放。同步 getter 尚未返回时仍占执行名额，不能通过取消请求提前腾出名额而无限增加阻塞任务。已就绪的聚合组可在准备容量满时直接复用。

Session 跟踪 ACK 任务，同一在途重发不重复追加等待；发送前再次检查请求身份。ACK 发送超时或失败会清理会话。聚合回调绑定具体实例，避免旧实例向新关系分发。条件比较区分字面路径与原始正则文本，签名使用长度编码，保留正则转义和大小写语义。

### 13.2 实际配置

| 配置 | 默认值 | 约束与作用 |
|---|---|---|
| Core `GlobalConfig.ResourceSlowGetThresholdMs` | 100ms | 正整数；Set/Populate 中缺失、非法或非正数回退 100 |
| WS `MaxConcurrentSubscriptionPreparations` | 4 | 大于 0；限制同时执行的 Core 准备任务 |
| WS `MaxPendingSubscriptionPreparations` | 256 | 非负；限制排队准备；独立未完成请求还受两项容量之和限制 |
| WS `SubscribeAckTimeout` | 10 秒 | ACK 等待时限 |
| WS `MaxSubscribeRetryCount` | 3 | 重试耗尽后沿用断开、重连恢复逻辑 |

准备容量拒绝的 Reason 为 `subscriptionPreparationCapacityExceeded`，连接保留，同 revision 重发复用拒绝结果。不能把发送请求或 ACK 接受等同于所有匹配资源观察成功。

Core 慢读监测使用单调计时、25ms 检查周期；日志在读取调用之外输出，不占设备读取锁。按资源每秒最多选择一次读取输出详细告警及完成耗时，其余以 suppressed 汇总；每次告警有独立 read 编号。报警不强制中断 getter，也不增加初读重试；线程调度可能推迟日志输出，不保证精确第 100ms 报警。

### 13.3 验证批次与复验

2026-10-09 的历史验证分为以下批次，均无失败、无跳过；数字表示测试结果数，包含参数化数据行。

| 批次 | Core 全量 | WS 全量 | 主要范围 |
|---|---:|---:|---|
| 首次 WS 接入与慢 Get 监测 | 185 | 222 | 异步准备、revision、容量、ACK、读取诊断与清理 |
| 覆盖补充 | 195 | 237 | 慢读边界、准备竞争、真实客户端重连及已知重复路由 |
| 自动观察列表接入 | 218 | 243 | 新增 Core 23 项、WS 6 项；黑名单事件转发及常驻观察生命周期 |

Core 仓库内的验证入口为 `Code/CodeTest/Event/RegexResourceObservationTests.cs`、`SlowResourceReadTests.cs`、`SlowResourceReadIntegrationTests.cs`、`SlowResourceReadCoverageTests.cs` 及 `AutomaticObservationListTests.cs`。在 Core 仓库根目录执行：

```powershell
dotnet test Code/CodeTest/CodeTest.csproj
```

WS 端到端测试属于独立仓库 websocketcommunicationmodule，包括 `WebSocketRegexSubscriptionLifecycleTests.cs`、`WebSocketRegexSubscriptionEndToEndTests.cs`、`WebSocketRegexCoverageTests.cs`、`WebSocketRegexCoverageEndToEndTests.cs` 和 `WebSocketAutomaticObservationListTests.cs`。在具备其所需 Core 项目依赖的 WS 检出环境中，从 WS 仓库根目录执行：

```powershell
dotnet test TestCode/TestCode.csproj
```

仅检出 Core 仓库即可运行 Core 测试；WS 历史通过数不表示 WS 测试源码或执行环境随 Core 仓库提供。以上保留历史结果与复验入口，不依赖开发机器上的临时 TRX 文件。

### 13.4 保留的边界

- 同步 getter 无法强制取消；初读失败不自动重试，复用既有聚合项不会重新初读失败资源。
- 需要必填参数的 getter 不自动轮询。精确订阅缺参初读仍可能失败，正则事件过滤可接收具体参数路径上的主动事件。
- 共享轮询不等于事件去重：两个不同 regex 命中同一事件时，服务端按各远端 Token 发送；客户端现有路由可能使本地匹配回调重复。本阶段未实现跨聚合去重，不承诺每个订阅只回调一次。
- Core 部分初读错误仍只返回通用无效样本原因，原始错误详情保留尚待修复。
- 自动观察列表已实现，详见 [自动观察列表实现方案](../自动观察列表/自动观察列表实现方案.md)。黑名单不屏蔽主动事件；WS 退订不释放应用持有的常驻观察。精确订阅仍返回一次有效初值，命中黑名单后不建立周期轮询。
- 验证覆盖功能和受控并发时序，不等于实际硬件负载、长期吞吐或线程饥饿下的硬实时保证。

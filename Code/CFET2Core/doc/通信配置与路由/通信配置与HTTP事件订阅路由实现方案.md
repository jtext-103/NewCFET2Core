# 通信配置与 HTTP 事件订阅路由实现方案

日期：2026-10-13  
状态：已实现并完成本地验收；未提交或发布。

文档归属：Core 公共配置和事件路由由 newcfet2core 管理，因此本方案保存在 Core 文档目录；WS 与标准应用为配套接入。下述跨仓库命令从多仓库工作区根目录执行，单独检出 Core 时不包含 WS 和应用项目。

## 1. 范围与确认规则

实现 GlobalConfig 通信配置、HTTP 地址事件订阅路由和 WS 接入。MQTT Broker、事件载荷、观察控制及资源租约不在本阶段范围。

HTTP 地址统一按配置选择 WS 或 MQTT 事件传输，普通 HTTP Get/Set/Invoke 不变；SubscribeResource 的初读仍走 HTTP。此选择不是全局远程接收限制，显式 WS、MQTT 和自定义协议订阅保持。双协议发布时 HTTP 订阅只选择一个渠道；显式建立多个渠道订阅可能重复接收，不提供跨协议去重。

## 2. 配置与公共接口

```json
{
  "EventPublishTransports": ["WebSocket"],
  "HttpEventSubscriptionTransport": "WebSocket",
  "WebSocketPortOffset": 1
}
```

新增字段片段不替代 IP、Port、Accept 等既有字段。

- 公共 EventTransport 枚举为 WebSocket、Mqtt，JSON 使用枚举名称字符串，解析忽略大小写，不接受枚举数字。
- GlobalConfig.EventPublishTransports 为只读集合，允许单项或两项，禁止空集合、null、重复项及未知值；setter 复制输入。
- HttpEventSubscriptionTransport 为单值；WebSocketPortOffset 为正整数，计算后的 HTTP/WS 端口均须为 1–65535。
- 缺省值如上；Get 输出规范名称及发布集合 JSON 数组字符串，Set 接受字典接口，Populate 要求 JSON 数组、字符串和整数类型。显式非法值拒绝，全部字段先校验后写入。
- 公共不可变 EventCommunicationConfiguration 提供三个生效属性、GetWebSocketPort(int) 及 EnsureStandardHostSupported()。构造参数同样校验并复制。CommunicationManager 在 Core 创建时捕获快照，Hub.ActiveEventCommunicationConfiguration 提供查询。修改静态 GlobalConfig 仅对新 Core 生效，应用须重启。
- IEventCommunicationModule 新增非抽象 EventTransport? Transport，默认 null，旧自定义模块不必改写。WS 返回 WebSocket，MQTT 骨架返回 Mqtt。

## 3. 路由和应用接入

1. CommunicationManager 根据生效 HttpEventSubscriptionTransport 选择传输标识表中的模块。http 别名不再进入事件协议注册表；多个模块声明 http 不影响路由。同传输标识重复注册抛 InvalidOperationException。事件协议名按忽略大小写校验；与已注册协议冲突或模块内重复时记录 Error 并抛 InvalidOperationException，消息包含协议名和模块类型。全部校验在 Hub 注入和注册表写入前完成，拒绝后不留下传输或协议登记；http 别名允许共享。生命周期枚举包含仅有 http 别名的已标识模块。
2. EventFilter 的 HTTP 地址转换、EventHub 手工指定 HTTP Host 的订阅及 SubscribeResource 前置检查使用同一模块选择。非 HTTP 协议继续按协议表处理；转换不改变资源表达式和参数路径。Token 记录实际事件协议，退订按原所属模块释放。
3. 缺失模块或转换返回空 Host 时抛 ProtocolNotSuportedException，不降级本地订阅；SubscribeResource 在初读前校验路由与目标端口。既有模块 Subscribe 运行期异常的清理及返回行为不在本阶段重构。
4. WS HTTP 地址转换使用 Hub 的生效偏移，显式 WS 地址端口不变。未注入 Hub 的独立 WS 实例使用构造时捕获的配置，保留直接 GetFullHost 的使用方式。
5. 标准应用在 AddThings 起始检查 EnsureStandardHostSupported，出现 MQTT 发布或 HTTP 订阅走 MQTT 时抛 NotSupportedException，不注册骨架或回退 WS。检查发生在启动通信和 Thing.Start 前。
6. 标准应用 WS EnableServer 根据发布集合控制，WS 客户端仍支持 HTTP 订阅及显式 WS 订阅；监听端口和地址转换使用同一配置快照。当前环境覆盖仅涉及分发并发，不覆盖监听或启用状态。

本阶段可解析及测试双协议配置，但标准应用不能运行 MQTT 配置；MQTT 路由通过模拟模块验证，实际通信另阶段完成。未新增 Broker 或租约字段，不改变 HTTP/WS 协议载荷、时间戳、观察资格或参数编码。

## 4. 实施步骤

- 修正工作区汇总文档的全局接收拦截描述，固定默认值与生效方式。
- 实现配置类型、解析、校验、快照及公共查询；统一 HTTP 模块选择和失败行为。
- 接入 WS/MQTT 模块标识、WS 地址偏移和标准应用注册，保留显式协议行为。
- 增加配置/路由单测及真实 WS 精确和正则测试，修正旧测试将 Get 字符串字典直接当作 JSON 字段类型的构造方式。
- 完成构建和全量回归后记录实际结果，同步汇总状态。

## 5. 验收要求与证据

Core EventCommunicationRoutingTests 覆盖配置缺省和往返、数组复制、非法输入及 JSON 类型、创建快照、注册顺序、精确和正则 HTTP 路由、手工 HTTP Host、显式 WS、重复注册、退订、HTTP 初读和路由不可用时无初读、未知协议拒绝及标准应用 MQTT 前置检查。

WS WebSocketHttpRoutingTests 使用真实网络连接覆盖非默认偏移、Core 快照优先、HTTP 精确/正则订阅、参数化主动事件、源端时间戳、不调用参数 getter 以及退订清理。

### 实际命令与结果（2026-10-13）

以下命令在工作区根目录执行：

```powershell
dotnet build newcfet2core/Code/CFET2Core/CFET2Core.csproj --no-restore -v quiet -p:WarningLevel=0
dotnet test newcfet2core/Code/CodeTest/CodeTest.csproj --no-restore -v quiet -p:WarningLevel=0 --logger 'trx;LogFileName=CommunicationRouting-Core.trx'
dotnet test websocketcommunicationmodule/TestCode/TestCode.csproj --no-restore -v quiet -p:WarningLevel=0 --logger 'trx;LogFileName=CommunicationRouting-WS.trx'
dotnet build newcfet2app/CFET2APP/CFET2APP.csproj --no-restore -v quiet -p:WarningLevel=0
```

- Core netstandard2.0 构建成功，标准应用构建成功，均无错误。
- Core 全量 282/282 通过，无失败或跳过；新增通信配置和路由专项 6 项。
- WS 全量 284/284 通过，无失败或跳过；新增真实网络路由专项 2 项。
- 结果文件分别为 newcfet2core/Code/CodeTest/TestResults/CommunicationRouting-Core.trx 和 websocketcommunicationmodule/TestCode/TestResults/CommunicationRouting-WS.trx。
- 实际标准应用进程检查：在临时工作目录写入 GlobalConfig.json，分别配置双协议发布、HTTP 订阅走 MQTT，运行 `dotnet <工作区>/newcfet2app/CFET2APP/bin/Debug/net10.0/CFET2APP.dll --no-cli`。两次均以非零退出码结束，异常栈包含 EnsureStandardHostSupported 和 AddThings，消息为 MQTT event communication is not implemented；未启动通信或 Thing.Start。
- Core 和 WS 的 git diff --check、应用本次源文件的 diff --check 通过。MQTT 目录不具备 Git 元数据，未声称该目录通过 Git 检查。
- 首轮 Core 两项旧配置文件测试失败：其把 Get 字符串字典直接序列化为 JSON，发布集合和端口偏移类型错误。已改成 JSON 数组和整数，保留新专项的错误类型拒绝验证。
- 首轮 WS 全量一项旧退订测试出现 NullReferenceException：测试服务器并发写入普通 List，断言同时读取。消息记录改为 ConcurrentQueue 并公开数组快照，不修改生产退订逻辑；修正后全量通过。
- WS 构建报告 NU1900（NuGet 漏洞数据源不可达），不影响本次构建及回归。现有工作区 WS 项目已使用本地 Core ProjectReference；该依赖配置保持原样。验收不证明 MQTT 传输可用，也不表示已打包发布。


## 6. 配置使用与后续边界

旧配置文件无需添加字段，默认保持 WS 发布、HTTP 事件订阅走 WS、偏移 1。若要偏移 7，在创建 Core 前加载 `WebSocketPortOffset: 7`，HTTP 来源端口 8002 会映射到 WS 8009；同一系统的偏移一致性由部署保证。显式 `ws://source:9000/...` 继续使用 9000，不叠加偏移。

业务组件通常继续提交 HTTP 资源地址，无需选择事件模块。自定义事件模块的非 HTTP 协议行为保留；要参与 HTTP 事件路由，模块需声明 Transport，旧仅声明 http 且 Transport 为 null 的模块不会被选中。声明传输后必须能够将来源 HTTP 地址转换为自身支持的事件 Host。

后续 MQTT 实现需保留已确定的来源身份、传输标识及 HTTP 路由入口，并在完成真实通信能力后移除标准应用的 MQTT 未支持检查。双协议发布配置目前仅可解析和通过模拟模块验证，不代表实际双协议发布已经可用。


### 注册冲突 review 修复验收（2026-10-13）

- 协议冲突由原先仅记录日志改为注册前明确拒绝，失败不污染任一注册表；共享 http 别名保留。增加 ProtocolConflictRejectsEntireRegistrationAndKeepsRoutesUsable，覆盖大小写冲突、模块内部重复、失败后路由可用及不启动失败模块。原 DuplicateEventProtocolRegistrationShouldKeepFirstModule 更新为同时断言异常及保留第一个模块。
- 使用第 5 节相同命令，将日志文件名改为 CommunicationRouting-Conflict-Core.trx 和 CommunicationRouting-Conflict-WS.trx：Core 283/283、WS 284/284 通过，无失败或跳过；应用构建和 Core diff --check 通过。

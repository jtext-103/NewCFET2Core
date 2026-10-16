# CFET Core 生命周期与事件出口设计方案

日期：2026-10-15  
状态：Core 生命周期调整已实施并通过回归。MQTT 连接、来源 ready 调度等运行时要求仍留在后续 MQTT 阶段。

## 1. 设计结论

Thing.Start 可以发布事件，因此通信模块接收本地事件所需的回调、队列等设施必须在第一个 Thing.Start 调用之前准备完成。

本地事件出口就绪与资源就绪是两个不同的生命周期边界：

- 事件出口就绪：通信模块已经可以接受 Core 交付的本地事件。
- 资源就绪：全部 Thing.Start 成功返回，自动观察初始化完成，可以处理依赖整体资源目录和设备启动状态的工作。

两个 Core observer 登记接口均只允许在启动 Thing 前使用。进入 StartThings 时，在调用任何 Thing.Start 前关闭登记窗口、固定回调集合。运行期不得新增或单独取消，生命周期跟随 Core；错误时间登记直接抛异常。

Broker 已连接不是本地事件出口就绪的必要条件。MQTT 可以先准备本地接收设施，再后台连接 Broker；Broker 暂时不可用不阻止应用启动。

## 2. 生命周期顺序

| 阶段 | 操作 | 约束 |
|---|---|---|
| 配置与装配 | 创建 Core、添加 Thing、执行 TryInit、登记资源路径、添加通信模块 | 建议仅完成配置与初始化，不发布业务事件、不启动持续采集 |
| 通信准备 | 注册事件和资源就绪 observer，准备内部队列，启动连接任务 | 返回前必须具备本地事件接收能力；外部连接可尚未完成 |
| 固定登记 | StartThings 入口关闭两个 observer 登记窗口，固定回调集合，建立事件交付的同步边界 | 必须先于任何 Thing.Start；此后登记直接失败 |
| Thing 启动 | 顺序调用各 Thing.Start | 允许发布事件及启动后台任务；整体资源尚未 Ready |
| 自动观察初始化 | 所有 Thing.Start 成功后初始化 AutoObserveList | 可以执行符合条件的 getter，观察可能产生事件 |
| 资源就绪 | 设置 IsLocalResourcesReady=true，同步通知就绪 observer | 回调在锁外执行一次；异常隔离并记录日志 |
| 正常运行 | 资源操作、事件交付、观察需求及实验控制 | observer 集合固定；普通业务订阅仍支持动态登记和取消 |
| 退出 | 撤销就绪、拒绝新工作、停止观察及事件交付，关闭 Thing 和通信模块，清理回调引用 | 已进入的同步回调允许结束，不能强行中止 getter |

```text
配置与装配
    ↓
StartCommunication：完成本地接收准备，外部连接可异步继续
    ↓
StartThings 入口：关闭 observer 登记，固定集合，开启事件交付
    ↓
依次 Thing.Start：允许发布启动事件
    ↓
自动观察初始化
    ↓
资源 Ready：通知通信模块可以处理观察需求
    ↓
正常运行 → Core 退出
```

资源就绪不代表实验已开始。开始实验、开始采集等业务阶段由 Thing 的业务接口控制。如果 Thing 在 Start 中启动持续采集，就意味着它选择随应用启动运行，不能以实验人员尚未操作为由假定此时没有事件或并发。

## 3. 接口契约

保留以下公开接口，不恢复 IDisposable 形式的独立登记句柄：

```csharp
public void RegisterLocalEventObserver(Action<EventArg> callback);
public void RegisterLocalResourcesReadyObserver(Action callback);
public bool IsLocalResourcesReady { get; }
```

### 3.1 登记窗口

- Core 创建后、StartThings 关闭登记窗口之前允许登记。
- 通信模块应在 StartCommunication 阶段完成登记。
- Thing.Start 内、就绪回调内及运行期间登记均抛 InvalidOperationException。
- 启动失败后不重新开放登记窗口；关闭后登记抛 ObjectDisposedException。
- null 回调抛 ArgumentNullException。
- 同一个 delegate 重复登记仍代表多个登记项，不自动合并。
- 普通业务 Subscribe 和 Token 取消不受此窗口限制。

关闭窗口与登记必须经过同一同步边界。并发登记要么在关闭前完整成功并进入固定集合，要么明确失败，不能出现成功返回却漏掉回调的情况。

### 3.2 StartCommunication

返回前应完成本地事件接收设施及回调登记。网络连接可以后台继续，不要求等待 Broker 连接成功。

配置错误、队列或本地接收设施准备失败应使启动失败，宿主不继续进入 Thing.Start。Broker 暂时不可用属于可恢复传输状态，继续应用启动并重连。

没有启用本地事件出口的通信模块时，允许以空集合启动；Core 的本地业务事件仍可正常工作。

### 3.3 StartThings

保持宿主调用形式：

```csharp
hub.StartCommunication();
hub.StartThings();
```

StartThings 在执行第一个 Thing.Start 之前固定两个 observer 集合。此后启动事件可以交付给本地事件 observer，无需等待资源 Ready。

全部 Thing.Start 和自动观察初始化成功后，设置资源 Ready 并通知已登记回调。启动失败时保持 Ready=false，登记窗口保持关闭，传播原始启动异常；宿主进入清理流程。不得因为失败而允许再次登记或直接重试同一 Core 的启动。

## 4. 资源访问的边界

当前代码中 TryAddThing 的顺序是：注入 Hub → 探测资源 → TryInit → 设置路径并加入 Core 资源字典。TryInit 执行时，该 Thing 自身尚未完成加入 Core 资源字典。

| 状态 | 含义 |
|---|---|
| TryAddThing 返回 | 资源路径已登记，可以被 Core 查找；getter 是否成功取决于 Thing 的初始化要求 |
| 某个 Thing.Start 返回 | 该 Thing 的同步启动步骤完成；其他 Thing 可能尚未完成 |
| IsLocalResourcesReady=true | 整体启动和自动观察初始化完成，可以开始依赖整体就绪状态的工作 |

当前资源访问入口没有统一使用 Ready 拦截；本方案不自动增加全局 Get/Set/Invoke 拦截，也不保证任意资源在 Ready 前一定不可访问。启动期间的内部资源依赖仍需 Thing 自己遵守初始化顺序。

Ready 不是每个 getter 成功或设备永远在线的保证。个别自动观察资源初读失败可以保留诊断而不阻止整体就绪，沿用现有 Core 行为。

## 5. 固定集合与并发设计

现有实现为了允许 Thing.Start 期间继续登记，在登记时替换数组，在每次交付时使用 Volatile.Read 读取快照。新的生命周期不再需要支持这种运行中的集合更新。

实施方向：

1. 登记阶段收集回调，在关闭窗口时一次性固定集合。
2. 通过明确的同步机制，将固定集合安全发布给事件处理线程，再允许交付。
3. 事件交付只遍历固定集合，不逐事件 ToArray，不为 observer 登记获取锁。
4. 不再为了启动期间新增登记而逐事件 Volatile.Read 可替换数组。
5. 退出仍需要关闭状态与事件线程之间的同步；不能把线程安全理解为删除所有锁和内存可见性机制。

不得仅删除现有 Volatile.Read/Write，然后依赖普通 bool、启动先后直觉或人工等待。事件处理线程可能早于 Thing.Start 创建，必须实际建立安全发布边界。具体同步手段在实施时结合事件循环确定。

固定集合不意味着可以在退出时并发修改数组元素。清理引用应与停止交付协调；已经进入的回调允许结束，每次后续交付仍需遵守关闭状态。

事件出口继续同步调用通信模块回调，回调只做快照、编码和有界入队，不等待网络、不调用业务 getter。单个出口异常隔离并输出日志，不影响其他出口和普通业务事件分发。

## 6. MQTT 对接

MQTT 在 StartCommunication 阶段登记一次，Broker 重连时复用登记，不重新向 Core 注册。

- 发布启用：提前准备本地事件接收设施，接收 Thing.Start 期间产生的事件。
- Broker 未连接：遵守首版离线事件不补发的策略。本地接收准备完成不等于远端订阅者必然收到事件。
- Core 尚未 Ready：不处理依赖来源资源的 observe 需求，也不宣布来源 ready。
- Core Ready、Broker 已连接、自身 observe Topic 的 SUBACK 成功：发布来源 ready 通知。
- Core 退出：停止接收新工作、停止调度并释放 MQTT 持有的观察句柄，配合 Core 完成关闭。

远端事件不进入本地事件出口，避免回流。Core 不依赖 MQTT 类型，其他通信模块可复用相同生命周期约定。

## 7. 与调整前实现的差异及实施范围

| 项目 | 调整前实现 | 本方案（Core 已实施） |
|---|---|---|
| observer 登记截止点 | 资源 Ready 或启动失败 | StartThings 入口、首个 Thing.Start 之前 |
| Thing.Start 中登记 | 允许 | 抛异常 |
| 启动期间事件 | 可交付，回调集合可能继续变化 | 可交付，回调集合已经固定 |
| 数组发布 | 每次登记替换数组 | 关闭登记窗口时固定并安全发布 |
| 资源 Ready | 所有 Thing.Start 与自动观察初始化后 | 保持相同含义，不再承担关闭登记窗口的职责 |

本轮修改涉及 EventHub.LocalObservers.cs、Hub.ResourceReadiness.Partial.cs、Hub.Modules.Partial.cs，以及事件循环启用和退出交付的必要协调。现有公开接口名称可以保留。

已调整原先允许 Thing.Start 内登记及启动期间新增登记的测试，并更新 MQTT 文档中的旧截止点描述。验证结果见第十节。

## 8. 后续验收要求

- 首个 Thing.Start 发布的事件能被预先登记的 observer 接收。
- Thing.Start、Ready 回调及运行期新增两种 observer 均明确失败。
- 登记与 StartThings 并发时，成功登记不遗漏，关闭后登记不混入固定集合。
- 事件线程早于 StartThings 启动时，仍能正确看到固定集合。
- 资源 Ready 严格晚于全部 Thing.Start 和自动观察初始化。
- 启动失败保持未就绪，窗口不重开，原始异常不被替换。
- Broker 不可用不阻止应用启动，也不误报 MQTT 来源 ready。
- 退出后停止后续交付，已进入回调可以结束；回调异常及回调内关闭不能死锁。
- 空集合、多 observer、重复 delegate、远端事件不回流等行为保持正确。
- 验证事件出口热路径不为登记加锁、不逐事件复制数组。
- 运行相关 Core 测试及 WS/MQTT 回归；真实 Broker 验证随 MQTT 运行时阶段完成。

装配期提前发布事件的处理方式已确认并实施：保留普通业务事件分发，不进入通信模块本地事件出口、不补发。入队时记录出口交付资格，避免积压事件在 StartThings 后被错误交付。Thing.Start 期间允许正常发布事件。

## 9. 关联文档

- [MQTT 完整方案](../../mqttcommunicationmodule/Doc/MQTT实现方案.md)
- [MQTT 框架与文件设计](../../mqttcommunicationmodule/Doc/MQTT框架与文件实现设计.md)
- [MQTT Core 接入实现方案与历史验证记录](../../mqttcommunicationmodule/Doc/MQTTCore接入实现方案.md)

关联文档已同步新生命周期；Core 接入记录第四、六节保留历史实现及当时测试结果，第七节记录本次实施。

## 10. 本次实施与验证

- StartThings 入口调用 BeginResourceStartup，在同一状态锁内固定两个 observer 集合并关闭窗口。
- 本地事件数组通过 volatile 启动标志安全发布，通知不再 Volatile.Read 数组引用，不逐事件 ToArray、不获取登记锁。
- Ready 只更新资源状态并调用启动入口已固定的回调；失败不重开窗口，重复 StartThings 不破坏已运行 Core 的 Ready 状态。
- 启动前事件保留普通业务交付；发布时记录出口资格，积压不改变资格。
- Core 专项 44/44、Core 全量 327/327、WS 290/290、MQTT 279/279 全部通过；标准应用 Release 构建成功（25 条警告、0 错误）。
- 实际命令、日志位置和限制见 [Core 接入验证记录第七节](../../mqttcommunicationmodule/Doc/MQTTCore接入实现方案.md)。

保留已有资源访问语义，没有增加全局 Get/Set/Invoke 的 Ready 拦截。MQTT Broker 不可用时的运行和来源 ready 三条件调度属于尚未实施的 MQTT 运行时，本次 Core 回归不证明真实 Broker 通信完成。

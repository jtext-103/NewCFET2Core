# CFET Core 当前生命周期说明

本文描述当前代码的生命周期与调用顺序。

## 1. 整体顺序

```text
加载配置、创建 Core
    ↓
添加 Thing 和通信模块
    ↓
StartCommunication：启动通信模块
    ↓
StartThings 入口：固定 observer 集合，关闭登记窗口
    ↓
依次执行 Thing.Start
    ↓
初始化自动观察
    ↓
IsLocalResourcesReady = true，通知资源就绪
    ↓
正常运行
```

标准应用先调用 `StartCommunication()`，再调用 `StartThings()`。

## 2. 创建和装配

标准应用先加载 GlobalConfig，再创建 Core。Core 创建时启动本地和远端事件处理循环，但此时本地事件 observer 出口尚未开启，资源就绪状态为 false。

`TryAddThing()` 的主要顺序是：

1. 向 Thing 注入 Hub。
2. 探测 Thing 的资源成员。
3. 执行 `Thing.TryInit()`。
4. 设置路径，将 Thing 和资源加入 Core 资源字典。

因此，`TryInit()` 执行时，该 Thing 自身尚未完成加入 Core 资源字典。`TryAddThing()` 返回后，资源路径已经可以查找，但 getter 能否成功取决于 Thing 自身是否已经准备好。

装配期间可以登记两种 Core observer：

```csharp
hub.RegisterLocalEventObserver(eventArg => { /* 接收本地事件 */ });
hub.RegisterLocalResourcesReadyObserver(() => { /* 接收就绪通知 */ });
```

它们返回 void，生命周期由 Core 管理，不提供独立取消句柄。同一 delegate 登记两次，会作为两个登记项调用。

## 3. 通信启动

`StartCommunication()` 先将 Core 的远端事件接收方法注册到事件通信模块，再调用通信管理器启动模块。

通信模块使用 Core observer 时，在这一阶段完成登记。通信启动先于 Thing 启动，使模块可以提前准备事件接收设施。

`StartCommunication()` 不设置资源 Ready。其返回也不统一代表外部服务已经连接成功，具体连接状态由通信模块管理。

## 4. Thing 启动

`StartThings()` 首先固定两种 observer 集合并关闭登记窗口，然后添加根 Thing、校验 minDelta 配置，再按登记顺序调用各个 `Thing.Start()`。

从入口固定集合开始，本地事件出口可以交付事件。`Thing.Start()` 内可以发布事件，也可以启动后台任务，此时 `IsLocalResourcesReady` 仍为 false。

两种 observer 在 Thing.Start 期间和之后都不能新增，调用登记接口会抛出 `InvalidOperationException`。普通业务 `Subscribe` 及 Token 取消不受这个登记窗口限制。

### 本地事件的启动边界

| 发布时点 | 普通业务订阅 | 本地事件 observer 出口 |
|---|---|---|
| StartThings 入口固定集合之前 | 正常分发 | 不交付、不补发 |
| 固定集合之后，包括 Thing.Start 期间 | 正常分发 | 正常交付 |

事件入队时记录是否具有出口交付资格。启动前事件即使积压到启动后才处理，也不会进入出口。

出口回调在安排普通业务异步回调之前同步执行。单个出口回调异常会记录日志，不阻止其他出口和业务分发。远端接收的事件不进入这个本地出口。

## 5. 资源就绪

所有 `Thing.Start()` 成功返回后，Core 初始化 AutoObserveList。初始化流程完成后：

1. 设置 `IsLocalResourcesReady = true`。
2. 在 StartThings 调用线程上、状态锁外，依次执行已经登记的资源就绪回调。

就绪通知只执行一次。回调异常会记录日志并隔离，不撤销 Ready，也不阻止其他就绪回调。

Ready 表示整体启动和自动观察初始化完成，不代表实验已经开始，也不保证每个资源每次读取都成功。某个自动观察资源初读失败可以记录为诊断，而不使整体启动失败。

资源 Get/Set/Invoke 没有统一用 Ready 拦截。资源路径已登记、Thing 已启动、整体资源 Ready，是三个不同状态。

## 6. 正常运行与失败

正常运行时，observer 集合保持固定，业务订阅可以动态增加和取消。开始实验、开始采集等业务行为由 Thing 的接口控制；如果 Thing 在 Start 中开启采集，就会随应用启动运行。

如果 Thing.Start 或自动观察初始化流程抛出异常，StartThings 保留并向外抛出原异常，资源 Ready 保持 false，清理待执行的就绪通知。observer 登记窗口不会重新打开，已经登记的事件出口保留到 Core 关闭。

同一 Core 不允许重复调用 StartThings。重复调用会抛异常，不会把已经成功启动的 Core 改成未就绪。启动失败不会自动回滚已启动的 Thing，清理由宿主调用关闭接口完成。

## 7. 关闭

当前 Core 提供两个不同范围的关闭入口：

| 入口 | 执行内容 |
|---|---|
| `DisposeThings()` | 关闭两种 observer、使资源就绪查询返回 false；停止资源观察；按登记顺序 Dispose 实现 IDisposable 的 Thing |
| `Hub.KillMaster()` | 停止日志清理；关闭 observer；Dispose EventHub 和通信管理器；释放 Core 单例引用 |

`DisposeThings()` 不负责关闭通信管理器，`KillMaster()` 不代替 Thing 的 Dispose。需要完整清理时，宿主负责调用相应入口；进程结束本身不等同于这些方法已被调用。

关闭后不再开始后续 observer 交付，新增登记抛出 `ObjectDisposedException`。已经进入的同步回调或 getter 允许结束，不会被强制中止。

创建新的 Core 会建立独立的 observer 和就绪状态。旧 Hub 上这几个生命周期接口仍绑定旧 Core，不会因为单例重建而转移到新 Core。

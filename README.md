# CFET2 Core

## 资源订阅的建立与发布

`SubscribeResource` 返回 `ResourceSubscription`，通过 `InitialValue` 读取初始值，通过 `Dispose()` 取消订阅。初始 Get 失败时抛错。远程返回只表示初始 Get 与本地事件注册完成，不等待远端成功 ACK；后续失败须查看 WebSocket Pending/Active/Failed 状态、LastError 和日志。初始 Get 与订阅并非原子操作，不保证覆盖两者之间的每次变化。

符合资格的未标记资源在存在观察需求时自动观测；资源订阅、独立观察句柄和 `AutoObserveList` 常驻观察共享同一路径的任务，最后一个观察需求释放后停止。所有含参数 getter（包括默认参数和 `params`）都不自动观测。标记 `[Cfet2Event]` 的资源由 Thing 在更新后显式调用 `MyHub.PublishResource(GetPathFor(nameof(资源成员)), value)`；属性标记本身不产生事件。Config 的分离 Get/Set 方法须把标记放在 Set 方法上。

`GlobalConfig.ResourceObservationIntervalMs` 默认 500 ms，仅接受正整数；`WebSocketResubscribeIntervalMs` 默认 10 ms，接受非负整数，由 APP 传给 WebSocket 模块。缺失或非法配置使用各自默认值；观测周期须在 Hub 创建前配置。

CFET2 的核心运行时。它提供 `Hub`、Thing、资源访问、Sample、中间件、通信模块管理和事件基础设施。

## 功能

- 支持 Thing 与 Hub 的挂载、导航和资源访问。
- 提供 `Status`、`Config`、`Method` 资源模型及 get/set/invoke 请求处理。
- 通过 `CommunicationManager` 管理资源通信模块和事件通信模块。
- 支持本地与远程事件订阅分离、Host 归一化与隔离、事件路由表和并发安全订阅清理。
- 支持日志、中间件、配置和 Sample 上下文。
- 已实现本地与远程 `SubscribeResource`、`[Cfet2Event]` 原生事件标识，以及符合资格的标量和容器资源内部周期观测。远程路径通过资源模块取得初始值，再经事件模块注册订阅；WebSocket 服务端已接入精确和正则资源订阅。完整 HTTP + WebSocket 应用链路的验证范围须以实际测试记录为准。
- 已实现 `AutoObserveList` / `AutoDenyList`、公共资源快照比较、`Thing.PublishResourceIfChanged`、minDelta 阈值和 HTTP 事件订阅传输选择配置。当前项目版本为 3.2.0，APP 和通信模块均固定引用 Core 3.2.0；旧版包不能作为这些能力的依据。

## 快速开始

安装 Core：

```powershell
dotnet add package CFET2Core --version 3.2.0
```

最小宿主示例：

```csharp
public sealed class MyHost : CFET2Host
{
    public void Configure()
    {
        MyHub.TryAddThing(new MyThing(), "/", "myThing");
    }
}
```

## 配置与扩展

- 库目标框架为 `netstandard2.0`；测试项目使用 .NET 10。
- `EventHub.Subscribe(EventFilter, Action<EventArg>)` 是已标记 `[Obsolete]` 的本地/远程事件过滤订阅接口，不等同于资源级 `Subscribe`，也不能订阅 `status`、`config` 等资源。
- 从源码运行测试：`dotnet test .\Code\CodeTest\CodeTest.csproj`。

## 资源自动观测的类型边界

`Hub.SubscribeResource` 对没有 `[Cfet2Event]` 且 getter 无参数的 Status/Config 资源，支持以下标量及容器：

- `byte`、`sbyte`、`short`、`ushort`、`int`、`uint`、`long`、`ulong`、`float`、`double`、`decimal`。
- `bool`、`char`、`string`、枚举。
- `DateTime`、`DateTimeOffset`、`TimeSpan`、`Guid`，以及上述值类型的 `Nullable<T>`。
- 数组（包括多维、锯齿数组）、精确的 `List<T>` 和 `Dictionary<TKey, TValue>`。元素及字典值递归支持上述标量和这三类容器；字典键仅支持上述标量及其可空值类型。

自动观测资格按 getter 的声明类型判断；`Status<T>`、`Config<T>`、`SampleBase<T>` 及其派生类按 `T` 判断。不支持自定义类/结构体、容器子类、接口声明、其他集合，以及 `object`、非泛型 `ISample` 等无法确定受支持值类型的声明。无参数的未标记资源在精确订阅时遇到这些声明，会抛出 `WrongResourceActionException`，不建立订阅或观测任务；正则组合返回该资源的 `Skipped` 结果并保留事件订阅。即使初值为 null，也按声明类型判断；容器复制时还会校验实际类型。含参数资源不轮询，精确订阅可读取指定参数的初值并接收对应路径的主动事件，详见订阅使用文档。

需要自动观测的旧资源应返回明确的受支持标量或容器类型，或具体的泛型 Sample；其他复杂资源可标记 `[Cfet2Event]` 并由 Thing 主动发布事件。仅添加属性不会自动产生事件，直接发布的复杂对象应保持稳定，发布后不要继续原地修改同一实例。该限制不影响普通 Get/Set。

比较基线保存独立快照。标量使用 `object.Equals`；数组和 List 比较形状或长度、顺序及内容，Dictionary 按键和值内容比较并保留键比较器。容器原地内容变化可触发 changed，内容相同的新实例不重复发布；null 与非 null 的转换视为变化。数值标量可通过 `ResourceMinDelta` 和 `ResourceMinDeltaOverrides` 设置发布阈值，默认 0；与上次成功入队的基准比较，容器和非数值仍使用内容比较，浮点边界不加 epsilon。轮询不能保证捕获两次采样之间的每一次变化。资源实现应保持 Sample 的值与其声明类型一致，并自行同步容器读写。

WebSocket 服务端无参数精确资源订阅复用同一校验，不支持的自动观测通过失败 ACK 返回；正则组合按逐资源结果跳过不合格资源。远程调用仍不等待成功 ACK。

`Thing.PublishResourceIfChanged` 为主动发布提供独立快照和按路径保存的比较基准，支持参数化事件路径，并使用同一 minDelta 配置；首次调用发布，返回 true 仅表示 Core 接受入队。直接 `Publish` / `PublishResource` 不执行阈值比较，也不更新该辅助函数的基准。

`AutoObserveList` 在全部 Thing.Start 成功后建立常驻观察；`AutoDenyList` 禁止自动轮询，不屏蔽主动事件。列表、阈值和通信选择均采用启动配置快照，修改后重建 Core 或重启应用。HTTP 事件订阅通过 `HttpEventSubscriptionTransport` 选取事件模块，WebSocket 地址使用 `WebSocketPortOffset`（默认 1）；标准宿主已支持 MQTT 单发布、WS/MQTT 双发布和 HTTP 事件订阅走 MQTT；HTTP 初读仍由资源模块完成。MQTT 配置随 Core 创建形成不可变快照，标准宿主始终注册 MQTT，默认 WS 配置也会异步连接 localhost:1883。配置示例见 [APP 事件通信配置](../newcfet2app/CFET2APP/Config/README.md)。

## 文档

### EventHub 关闭行为

`Init()` 重复调用不会创建额外事件线程。`Dispose()` 标记关闭、取消并唤醒本地/远程事件循环，等待它们退出后清理队列、等待句柄和取消对象；重复 Dispose 安全。关闭后的 Publish/远程事件输入被忽略，新的 Init/Subscribe 抛出 `ObjectDisposedException`，旧 token 可安全释放。

关闭不等待已经执行中的用户 handler，也不等待被业务阻塞的资源 getter；观测任务取消后，其取消对象在任务实际退出时释放。正常情况下 Dispose 返回时两条事件循环已结束；若循环被异常耗时的过滤或日志处理阻塞，最多等待 5 秒并告警，退出后再释放句柄。事件循环内触发关闭时也使用延迟释放，避免等待自身。关闭不保证排空待处理事件。

### 相关文档

- [订阅使用与参数边界](<Code/CFET2Core/doc/订阅使用/订阅使用设计文档.md>)
- [容器自动观察](<Code/CFET2Core/doc/自动观察引用类型/自动观察引用类型变化实现方案.md>)
- [自动观察列表](<Code/CFET2Core/doc/自动观察列表/自动观察列表实现方案.md>)
- [公共比较与主动变化发布](<Code/CFET2Core/doc/主动变化发布/公共比较与主动变化发布实现方案.md>)
- [minDelta 使用示例](<Code/CFET2Core/doc/主动变化发布/minDelta使用示例.md>)
- [通信配置与路由](<Code/CFET2Core/doc/通信配置与路由/通信配置与HTTP事件订阅路由实现方案.md>)
- [事件处理总体设计](<Doc/new doc/CFETCore事件处理总体设计.md>)
- [事件系统落地方案](<Doc/new doc/CFETCore事件系统落地方案.md>)
- [升级内容](<Doc/new doc/CFETCore升级内容.md>)
- [当前生命周期](Doc/CFETCore当前生命周期说明.md)
- [生命周期与事件出口设计](Doc/CFETCore生命周期与事件出口设计方案.md)
- [源端样本时间戳](<Doc/new doc/ISample时间戳.md>)
- [当前生命周期](Doc/CFETCore当前生命周期说明.md)
- [生命周期与事件出口设计](Doc/CFETCore生命周期与事件出口设计方案.md)
- [源端样本时间戳](<Doc/new doc/ISample时间戳.md>)
- [历史资料](<Expired/old doc/>)

## 构建与发布

当前 `.gitlab-ci.yml` 在默认分支执行版本更新、Release 测试、构建和打包，并在 publish 阶段推送 NuGet。Core、HTTP、WebSocket、MQTT 按此顺序处理依赖，先确认所需 Core 包可还原，再更新下游固定包版本。

提交说明末尾的 `-a` 升主版本并清零次版本和修订号，`-b` 升次版本并清零修订号；没有这两个末尾标记时升修订号。独立的 `-notest` 标记跳过测试，但仍执行构建、打包和发布；组合示例为 `发布更新 -notest -b`。CI 默认遍历仓库内测试项目，执行 `dotnet test --configuration Release`；没有配置真实 Broker 开关和工具时，不能将流水线通过视为 MQTT 网络集成验证通过。

# CFET2 Core

## 资源订阅的建立与发布

`SubscribeResource` 返回 `ResourceSubscription`，通过 `InitialValue` 读取初始值，通过 `Dispose()` 取消订阅。初始 Get 失败时抛错。远程返回只表示初始 Get 与本地事件注册完成，不等待远端成功 ACK；后续失败须查看 WebSocket Pending/Active/Failed 状态、LastError 和日志。初始 Get 与订阅并非原子操作，不保证覆盖两者之间的每次变化。

未标记资源在存在资源订阅时自动观测，相同路径共享任务，最后一个订阅释放后停止。标记 `[Cfet2Event]` 的资源由 Thing 在更新后显式调用 `MyHub.PublishResource(GetPathFor(nameof(资源成员)), value)`；属性标记本身不产生事件。Config 的分离 Get/Set 方法须把标记放在 Set 方法上。

`GlobalConfig.ResourceObservationIntervalMs` 默认 500 ms，仅接受正整数；`WebSocketResubscribeIntervalMs` 默认 10 ms，接受非负整数，由 APP 传给 WebSocket 模块。缺失或非法配置使用各自默认值；观测周期须在 Hub 创建前配置。

CFET2 的核心运行时。它提供 `Hub`、Thing、资源访问、Sample、中间件、通信模块管理和事件基础设施。

## 功能

- 支持 Thing 与 Hub 的挂载、导航和资源访问。
- 提供 `Status`、`Config`、`Method` 资源模型及 get/set/invoke 请求处理。
- 通过 `CommunicationManager` 管理资源通信模块和事件通信模块。
- 支持本地与远程事件订阅分离、Host 归一化与隔离、事件路由表和并发安全订阅清理。
- 支持日志、中间件、配置和 Sample 上下文。
- 已实现本地与远程 `SubscribeResource`、`[Cfet2Event]` 原生事件标识，以及无该标记的标量资源内部周期观测。远程路径通过资源模块取得初始值，再经事件模块注册订阅；WebSocket 服务端已接入资源订阅。完整 HTTP + WebSocket 应用链路的验证范围须以实际测试记录为准。

## 快速开始

安装 Core：

```powershell
dotnet add package CFET2Core --version 3.0.0
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

`Hub.SubscribeResource` 对没有 `[Cfet2Event]` 的 Status/Config 资源，仅自动观测以下标量：

- `byte`、`sbyte`、`short`、`ushort`、`int`、`uint`、`long`、`ulong`、`float`、`double`、`decimal`。
- `bool`、`char`、`string`、枚举。
- `DateTime`、`DateTimeOffset`、`TimeSpan`、`Guid`，以及上述值类型的 `Nullable<T>`。

订阅建立前通过 getter 的声明类型校验一次；`Status<T>`、`Config<T>`、`SampleBase<T>` 及其派生类按 `T` 判断。数组、集合、自定义类/结构体，以及声明为 `object`、非泛型 `ISample` 等无法确定标量类型的资源，抛出包含路径、类型和处理建议的 `WrongResourceActionException`，不建立订阅或观测任务。即使初值为 null，也按声明类型判断。

需要自动观测的旧资源应返回明确标量类型或具体的泛型 Sample；复杂资源可标记 `[Cfet2Event]` 并由 Thing 主动发布事件。仅添加属性不会自动产生事件，发布的复杂对象应保持稳定，发布后不要继续原地修改同一实例。该限制不影响普通 Get/Set。

比较基线保存独立的标量值，使用 `object.Equals`；null 与非 null 的转换会触发 changed，相同值不会重复触发。浮点值没有容差/死区。轮询不能保证捕获两次采样之间的每一次变化。资源实现应保持 Sample 的值与其声明类型一致。

WebSocket 服务端资源订阅复用同一校验，不支持的自动观测通过失败 ACK 返回；客户端等待 ACK 的语义不属于本次类型限制改动。

## 文档

### EventHub 关闭行为

`Init()` 重复调用不会创建额外事件线程。`Dispose()` 标记关闭、取消并唤醒本地/远程事件循环，等待它们退出后清理队列、等待句柄和取消对象；重复 Dispose 安全。关闭后的 Publish/远程事件输入被忽略，新的 Init/Subscribe 抛出 `ObjectDisposedException`，旧 token 可安全释放。

关闭不等待已经执行中的用户 handler，也不等待被业务阻塞的资源 getter；观测任务取消后，其取消对象在任务实际退出时释放。正常情况下 Dispose 返回时两条事件循环已结束；若循环被异常耗时的过滤或日志处理阻塞，最多等待 5 秒并告警，退出后再释放句柄。事件循环内触发关闭时也使用延迟释放，避免等待自身。关闭不保证排空待处理事件。

### 相关文档

- [事件处理总体设计](<Doc/new doc/CFETCore事件处理总体设计.md>)
- [事件系统落地方案](<Doc/new doc/CFETCore事件系统落地方案.md>)
- [升级内容](<Doc/new doc/CFETCore升级内容.md>)
- [历史资料](<Expired/old doc/>)

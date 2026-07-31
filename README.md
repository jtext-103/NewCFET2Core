# CFET2 Core

CFET2 的核心运行时。它提供 `Hub`、Thing、资源访问、Sample、中间件、通信模块管理和事件基础设施。

## 功能

- 支持 Thing 与 Hub 的挂载、导航和资源访问。
- 提供 `Status`、`Config`、`Method` 资源模型及 get/set/invoke 请求处理。
- 通过 `CommunicationManager` 管理资源通信模块和事件通信模块。
- 支持本地与远程事件订阅分离、Host 归一化与隔离、事件路由表和并发安全订阅清理。
- 支持日志、中间件、配置和 Sample 上下文。
- 当前未实现资源级 `Subscribe`（`AccessAction` 仅有 `get`、`set`、`invoke`）、`[CFET2Event]`、内部周期观测，以及这些能力与真实通信模块的完整整合。

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

## 文档

- [事件处理总体设计](<Doc/new doc/CFETCore事件处理总体设计.md>)
- [事件系统落地方案](<Doc/new doc/CFETCore事件系统落地方案.md>)
- [升级内容](<Doc/new doc/CFETCore升级内容.md>)
- [历史资料](<Expired/old doc/>)

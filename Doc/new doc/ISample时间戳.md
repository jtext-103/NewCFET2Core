# ISample.Timestamp

`ISample.Timestamp` 为可读写的 `DateTimeOffset?`，表示源端变化产生时间，读取和写入均归一化为 UTC。未知时间为 `null`。

## 赋值规则

- 开发者知道真实变化时间时，可在发布前设置该属性，框架保留该时刻。
- 主动发布未带时间的样本或原始值时，框架在本地发布入口补时间。
- 自动观察未带时间时，使用有效读取完成、发现数值变化的时间；轮询不能推断两次读取之间的真实物理变化时刻。
- 普通样本构造、资源读取、复制及远端接收不自动补时间。
- 本地发布快照复制 EventArg 字段及样本 Context，不回写调用方样本。Context 内部的业务对象不进行深复制；发布后应避免修改业务载荷。
- 接收方读取 `eventArg.Sample.Timestamp`。该时间不会被网络接收或回调执行时间覆盖。

```csharp
var sample = new Status<double>(23.5)
{
    Timestamp = changedAt // DateTimeOffset，设备或开发者提供的变化时间
};
MyHub.PublishResource("/device/temperature", sample);

// 没有设备时间时，直接发布值，框架使用本次发布时间。
MyHub.PublishResource("/device/temperature", 23.5);
```

## 存储与兼容

`SampleBase<T>` 将时间保存在 Context 的 `CFET2CORE_SAMPLE_TIMESTAMP` 键中，写入 UTC ISO 8601 `O` 格式字符串，保留 .NET ticks 精度。读取支持字符串、DateTimeOffset 和 DateTime；无时区字符串和 Unspecified DateTime 按 UTC 解释。缺失、空值或非法值返回 null，不使用当前时间兜底。设置 null 时移除该键。

样本复制和类型转换保留时间。HTTP JSON、MessagePack 通过 Context 传输；WebSocket 完整模式通过 Context 传输，精简模式通过可选 `timestamp` 字段传输，并恢复到样本。无 Context 的兼容样本对象也读取 `timestamp`。WebSocket 协议版本保持不变，旧消息仍可接收。MQTT event 消息保留源端 Timestamp 和完整 Context，接收后恢复为 Core 样本；完整、精简及 null 样本的规则见 [MQTT Protocol 方案](../../../mqttcommunicationmodule/Doc/MQTTProtocol实现方案.md)。MQTT event 消息保留源端 Timestamp 和完整 Context，接收后恢复为 Core 样本；完整、精简及 null 样本的规则见 [MQTT Protocol 方案](../../../mqttcommunicationmodule/Doc/MQTTProtocol实现方案.md)。

直接实现 ISample 的外部类型必须实现新属性并重新编译；元数据应遵循 Context 存储约定。继承 SampleBase<T> 的类型自动获得属性。旧客户端不会因为忽略新增字段获得时间戳能力，使用新接口需更新 Core 和对应通信模块。

## 联调构建

2026-10-23 当前依赖：HTTP、WebSocket、MQTT 和 APP 均固定引用 CFET2Core 3.2.0 NuGet 包，不再依赖同级 Core 源码。Core 自身测试仍构建本仓库源码；模块测试验证各自源码与固定 Core 包的组合。以下命令从多仓库工作区根目录运行：

```powershell
dotnet test newcfet2core/Code/CodeTest/CodeTest.csproj
dotnet test websocketcommunicationmodule/TestCode/TestCode.csproj
dotnet test httpcommunicationmodule/TestCode/TestCode.csproj
```

独立交付时先发布 Core，再更新和发布通信模块，最后更新 APP 包依赖。当前组合为 Core 3.2.0、HTTP 1.0.2、WebSocket 1.1.0、MQTT 1.0.0；直接实现 ISample 的外部类型仍需按新接口重新编译。


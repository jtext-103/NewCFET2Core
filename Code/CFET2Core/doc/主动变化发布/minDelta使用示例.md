# minDelta 使用示例

## 启动配置

在创建 Core／EventHub 前加载 GlobalConfig。下面是新增字段片段，实际配置文件仍需保留 IP、Port、Accept 等字段。

```json
{
  "ResourceMinDelta": 0,
  "ResourceMinDeltaOverrides": [
    { "Path": "/device/Temperature", "MinDelta": 1 }
  ]
}
```

也可在创建 Core 前使用代码配置：

```csharp
GlobalConfig.ResourceMinDelta = 0;
GlobalConfig.ResourceMinDeltaOverrides = new[] {
    new ResourceMinDeltaOverride("/device/Temperature", 1)
};
```

EventHub 创建时捕获配置；后续静态配置修改不会影响已有 Core。Path 是本地注册后的基础资源路径，单资源优先。所有参数通道共用阈值，但分别累计变化。

## 主动发布

```csharp
public class Device : Thing
{
    private readonly double[] values = new double[4];

    [Cfet2Status]
    public double Temperature(int channel) => values[channel];

    public void UpdateTemperature(int channel, double value)
    {
        values[channel] = value;
        PublishResourceIfChanged(nameof(Temperature), value,
            parameters: new object[] { channel });
    }
}
```

Device 挂载在 `/device` 后，辅助函数自动处理挂载路径、资源名及参数编码，无需手工拼接事件路径。首次调用发布；之后以该通道上次成功进入 Core 队列的值为基准。阈值 1、基准 10 时，10.4 和 10.8 不发布，11 发布后基准变为 11。返回 true 只表示本地入队，不表示远端收到。

带参数 getter 不支持自动观察，必须在获取具体值的位置主动发布。直接 Publish/PublishResource 不使用阈值，也不更新辅助函数基准。

## Core 自动观察

对于无参数、符合观察资格的 Status/Config，本地精确／组合订阅、独立观察句柄、AutoObserveList 及 WS 精确／正则订阅都使用同一阈值配置。配置本身不会启动观察，仍需有效观察需求。有效初读仅建立基准，不发布 changed。

只有数值标量使用阈值，数组、List、Dictionary 保持内容比较。阈值必须是有限非负 double，JSON 使用数字而不是字符串。浮点边界比较不加 epsilon；NaN/Infinity 状态转换视为变化，相同特殊值不重复发布。

详见 [实现方案](minDelta阈值发布实现方案.md)。

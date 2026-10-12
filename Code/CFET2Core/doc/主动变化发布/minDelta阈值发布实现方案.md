# minDelta 阈值发布实现方案

日期：2026-10-12  
状态：已实现并完成本地回归（2026-10-12）；尚未提交或发布

## 1. 目标与依据

依据工作区汇总文件《修改方案10.9.md》第 5、6、13 节及当前 Core 代码，为自动观察和 `Thing.PublishResourceIfChanged` 增加共用的数值阈值判断，减少小幅变化事件，并允许未发布的小变化累积。《修改方案10.9.md》位于多仓库工作区根目录，不属于 newcfet2core 仓库；本文件已包含实现所需规则，不依赖该工作区文件才能阅读或复验。

本方案不改变直接 `Publish`、WS 分发、资源参数编码和既有观察资格限制；不增加 MQTT、逐元素阈值、参数通道独立阈值或运行中配置热更新。

## 2. 当前实现基础

| 文件 | 当前能力 | 拟调整 |
|---|---|---|
| `Event/ResourceValueComparison.cs` | 公共快照及内容变化比较 | 保留原接口，新增阈值比较重载 |
| `Event/ObservationValueSnapshot.cs` | 标量、数组、List、Dictionary 独立快照及内容比较 | 保留内容比较语义 |
| `Event/ResourceObservationManager.cs` | 初读建立基准；轮询比较；TryPublish 成功才更新基准 | 取得资源阈值并调用新重载 |
| `Event/EventHub.ChangePublication.cs` | 按资源对象及完整参数路径保存基准；同路径调用串行化 | 取得基础资源阈值并调用新重载 |
| `Thing.cs` | 提供 PublishResourceIfChanged 公共入口 | 保留现有签名，补充说明 |
| `Config/GlobalConfig.cs` | 配置解析；观察列表支持 Core 创建时快照 | 新增阈值配置及独立不可变快照 |

发布成功仍以 `EventHub.TryPublish` 接受事件并进入 Core 队列为准，不表示订阅者或远端已经收到。

## 3. 配置入口和优先级

集中在 GlobalConfig 配置，第一版不增加 Attribute 或调用级覆盖。

```json
{
  "ResourceMinDelta": 0,
  "ResourceMinDeltaOverrides": [
    { "Path": "/device/Temperature", "MinDelta": 1 },
    { "Path": "/device/Pressure", "MinDelta": 0.05 }
  ]
}
```

以上为新增字段片段，实际文件继续保留 IP、Port、Accept 等既有配置。

### 3.1 配置语义

- `ResourceMinDelta`：全局阈值，未配置时为 `0`。
- `ResourceMinDeltaOverrides`：单资源覆盖列表，未配置或为 null 时为空。
- 覆盖项必须包含 `Path` 和 `MinDelta`，单资源优先于全局。
- 显式配置 `MinDelta: 0` 表示该资源值变化即发布，即使全局阈值大于零。
- Path 仅匹配本地注册目录中的 Status/Config 基础资源路径，使用注册后的实际路径和别名，不涉及 HTTP 地址转换。
- 使用 `StringComparer.InvariantCultureIgnoreCase` 精确查找及检测重复，不编译 regex，不解析参数、不补全路径、不去掉末尾斜杠。同一基础资源全部通道共用阈值。
- 阈值按资源值本身的单位解释，Core 不进行单位转换。

### 3.2 生命周期和校验

EventHub 构造时捕获独立、不可变的阈值配置快照，与现有观察配置初始化处于同一阶段，不能等到 StartThings 后才捕获。GlobalConfig 必须在 Core 创建前配置；后续修改不影响既有 EventHub，重建 Core 或重启后生效。

内部提供 `ResourceChangePublicationConfiguration`，保存 double 全局阈值及只读精确路径映射，并提供 `ResolveMinDelta(ResourceBase resource)`。自动观察和主动辅助函数都通过所属 EventHub 的同一快照取得有效阈值，不在发布时读取静态 GlobalConfig。

规则如下：

- 全局、单资源覆盖和内部有效阈值统一使用 double，默认 `0`；必须有限且非负，负值、NaN、Infinity 拒绝，`-0` 按零处理。
- Populate 的阈值只接受 JSON 数值，不接受字符串；Set(Dictionary<string,string>) 接受 invariant culture 数值文本；Get 输出可往返的 double 文本。
- 使用常规 invariant culture 数值解析，不要求准确十进制表示，不增加极小精度专用解析或 epsilon。
- 缺省全局字段为零，缺省或 null 覆盖列表为空；显式 null 全局阈值、null 单项阈值、缺失单项字段及未知单项字段拒绝。
- 新增配置全部校验成功后再写入，失败不部分更新配置；保留其他既有字段的解析语义。
- 保留空白路径和重复项检查，不新增复杂路径格式校验器。其余以本地注册目录精确匹配为准。
- 在 StartThings 注册 RootThing 后、调用各 Thing.Start 前检查覆盖目标；未匹配或目标不是 Status/Config 时记录一次 Warn，不阻止启动、不调用 getter、不自行建立观察。
- 目录检查仅提供诊断，不负责激活配置；StartThings 前的观察及 Thing.Start 中的主动辅助发布均已使用配置快照。
- 参数通道路径或带末尾斜杠的路径若没有匹配的基础资源，报告未匹配，不进行修正或转换。
- GlobalConfig.Get、Set、Populate 及快照复制应完整支持新增字段，并保持旧配置文件兼容。

### 3.3 公共代码配置入口及异常

新增公共不可变配置项和 GlobalConfig 属性；以下为接口摘要：

```csharp
public sealed class ResourceMinDeltaOverride
{
    public string Path { get; }
    public double MinDelta { get; }

    public ResourceMinDeltaOverride(string path, double minDelta);
}

// 以下属性位于 GlobalConfig 中。
public static double ResourceMinDelta { get; set; }
public static IReadOnlyList<ResourceMinDeltaOverride>
    ResourceMinDeltaOverrides { get; set; }
```

- 配置项构造时拒绝空白路径及非法阈值；全局阈值 setter 执行相同数值校验。
- 覆盖列表 setter 校验并复制列表，getter 返回只读列表；null 列表视为空，null 项及重复路径拒绝。配置项不可变，调用者修改原始列表不能影响已保存配置或 EventHub 快照。
- 代码配置的非法阈值抛出 ArgumentOutOfRangeException；非法路径、列表项或重复路径抛出 ArgumentException。失败的 setter 保留原值。
- JSON／字典解析错误统一抛出带字段名或列表下标的 ArgumentException，说明具体原因。

## 4. 如何取得当前资源的配置

业务代码无需自行读取 JSON 或拼接挂载路径。例如 Thing 挂载在 `/device`，声明资源 `Temperature(int channel)`：

```csharp
PublishResourceIfChanged(
    nameof(Temperature),
    temperature,
    parameters: new object[] { channel });
```

内部按以下步骤处理：

1. 复用现有资源解析流程，将标识解析为已注册的资源对象，并处理声明别名。
2. 用资源对象基础路径 `/device/Temperature` 查询 Core 配置快照；命中覆盖项时得到 `1`，否则使用全局阈值。
3. 复用现有参数校验和路径编码，形成本次完整事件路径。
4. 用完整事件路径对应的独立基准执行阈值比较。
5. 达到条件才发布；TryPublish 成功后更新该路径基准。

| 通道 | 配置查询路径 | 比较基准路径示意 | 阈值 |
|---|---|---|---|
| 1 | `/device/Temperature` | `/device/Temperature/1` | 1 |
| 2 | `/device/Temperature` | `/device/Temperature/2` | 1 |

配置查询使用基础路径，比较基准使用完整参数路径。表中参数路径为简单整数参数示意，实际编码继续沿用现有实现。

如果 Thing 挂载在 `/plant/device`，配置路径应为 `/plant/device/Temperature`；有资源别名时使用注册后的名称。不得通过裁剪字符串猜测基础路径，应直接使用解析所得资源对象。

## 5. 发布入口的生效范围

| 入口 | 是否使用阈值 | 首次行为 |
|---|---|---|
| 自动观察产生 changed | 是 | 有效初读建立基准，不发布 |
| PublishResourceIfChanged | 是 | 首次有效调用发布，入队成功后建立基准 |
| 直接 Publish | 否 | 保持现有行为 |

AutoDenyList 仅禁止自动观察，不禁止主动辅助函数。带参数 getter 仍不能自动观察，但可以通过主动辅助函数应用阈值。

自动观察与主动辅助函数分别保存基准，不相互覆盖。直接 Publish 不更新它们的基准。混合使用时，不保证两种入口之间消除重复事件。

## 6. 数值比较规则

先判断值是否变化，再判断差值是否达到阈值：

```text
值相等                       → 不发布
值变化且 minDelta = 0         → 发布
同类型有限数值标量且差值 >= 阈值 → 发布
同类型有限数值标量且差值 < 阈值  → 不发布
其他内容变化                  → 发布
```

### 6.1 类型范围

| 类型 | 行为 |
|---|---|
| byte、sbyte、short、ushort、int、uint、long、ulong | 应用数值阈值，精确保留整数差值 |
| decimal | 应用数值阈值，精确比较十进制差值 |
| float、double | 应用数值阈值，使用 double 差值与 double 阈值比较 |
| Nullable 数值 | 装箱后非 null 值按对应数值类型处理 |
| bool、char、enum、字符串、时间、Guid 等 | 保持原相等性比较，不应用数值阈值 |
| 数组、List、Dictionary | 保持整体内容比较，不递归应用阈值 |
| 不同运行时类型 | 沿用原行为，视为变化，不做跨类型换算 |

null 与 null 相等；null 与非 null 切换视为变化，不受阈值过滤。自定义对象及其他不支持类型继续遵循当前快照类型限制。

### 6.2 NaN、Infinity 和浮点边界

- NaN 与 NaN 视为相等，不重复发布。
- 相同符号 Infinity 视为相等，不重复发布。
- 有限值与 NaN/Infinity 切换、NaN 与 Infinity 切换、正负 Infinity 切换均视为状态变化，直接发布。
- 正零与负零相等。
- 有限浮点值使用实际浮点表示判断，不额外添加 epsilon；十进制字面量的数学差值与实际浮点差值可能不同。
- 有限 double 相减溢出为 Infinity 时，差值超过任意合法阈值，判定达到阈值。

### 6.3 避免精度丢失和溢出

不能把所有值统一转 double，否则 long、ulong 的相邻大整数可能丢失变化。也不能直接对整数或 decimal 执行 `Math.Abs(current - previous)`，因为相减及绝对值可能溢出。

使用 System.Numerics.BigInteger 精确表示整数及 decimal 差值：整数转为精确整数；decimal 通过系数、符号及尺度转换到公共十进制尺度。将有限 double 阈值分解为精确的二进制整数比，通过交叉相乘判断差值是否达到阈值。不得将资源值或差值统一转 double，也不得将阈值转换成 decimal，以免出现精度丢失、差值溢出或阈值超出 decimal 范围。实现时验证 netstandard2.0 构建兼容性。

以上是内部比较策略，不扩展配置精度要求。阈值遵循 double 表示精度；整数和 decimal 分支比较的是资源差值与实际 double 阈值，例如十进制 0.05 的 double 表示不一定恰好等于 decimal 0.05。float/double 分支使用 double 相减，不额外增加 epsilon。浮点边界误差限制应写入接口说明。

## 7. 公共比较接口兼容

保留原接口，新增重载，不用可选参数替换原方法：

```csharp
public static bool HasChanged(
    ResourceValueSnapshot previous,
    ResourceValueSnapshot current);

public static bool HasChanged(
    ResourceValueSnapshot previous,
    ResourceValueSnapshot current,
    double minDelta);
```

原接口继续提供现有内容变化比较。新增重载在阈值为零时与原接口结果一致；依次检查 previous、current 和 minDelta，再执行第 6 节规则。快照为 null 时抛出 ArgumentNullException；阈值为负数、NaN 或 Infinity 时抛出 ArgumentOutOfRangeException，即使值相等也不能绕过阈值校验。

比较接口只判断，不读取 GlobalConfig、不更新基准、不发布事件。阈值配置解析由 Core 负责，基准生命周期由各发布入口负责。容器快照及事件值副本隔离保持不变。

`Thing.PublishResourceIfChanged<T>(string resourceName, T value, object[] parameters = null, DateTimeOffset? timestamp = null)` 签名不变，不新增调用级 minDelta 参数。

## 8. 基准更新与并发

以阈值 1、基准 10 为例：

```text
10.4 → 不发布，基准仍为 10
10.8 → 不发布，基准仍为 10
11.0 → 入队成功后，基准变为 11
```

- 自动观察初读是唯一无需发布就建立基准的初始化行为。
- 主动辅助函数首次有效调用不执行差值过滤，仍尝试发布。
- 读取失败、快照失败、未达到阈值或入队失败均不更新基准。
- 主动辅助函数保留现有同完整路径锁，将比较、入队和基准更新保持在同一临界区。
- 不新增跨通道锁；配置快照只读，不在发布热路径重新解析 JSON。
- 发布时间戳变化不单独触发值变化发布，沿用当前值比较行为。
- 观察最后引用释放后重新建立观察，仍执行初读初始化；不把主动辅助函数基准与观察生命周期合并。

## 9. 实施步骤

1. 实现 double 阈值公共配置类型、常规数值解析、复制和错误行为；接入 GlobalConfig 的 Get/Set/Populate，在 EventHub 构造时捕获只读快照，并在 StartThings 的指定阶段进行目录诊断。
2. 新增 double 参数的 HasChanged 阈值重载及数值差值实现，保留旧接口；整数和 decimal 差值与 double 阈值通过整数比比较。
3. 自动观察入口取得有效阈值并调用重载，保持初读及 TryPublish 后更新规则。
4. 主动辅助函数通过已解析资源取得相同配置，保持参数路径、首次发布、并发及异常行为。
5. 补充开发示例、配置说明和公共接口 XML 文档。
6. 执行各观察入口的集成验收、针对性测试、netstandard2.0 构建及 Core、WS 全量回归；记录实际测试命令、结果及覆盖入口，不沿用旧测试数量作为本次验证证据。

## 10. 验收用例

| 类别 | 必须覆盖的行为 |
|---|---|
| 默认兼容 | 无新增配置、阈值为 0、旧 HasChanged、原辅助函数调用保持原行为 |
| 配置优先级 | 全局值、单资源覆盖、显式 0 覆盖、大小写匹配、挂载路径和别名 |
| 配置校验 | 非有限值、负值、-0、重复路径、空白路径、null 项、字段缺失、未知字段、解析错误的异常类型及定位信息；失败不部分写入 |
| 配置往返 | JSON 数值与字符串区分、invariant culture 解析、Get/Set/Populate 往返、旧配置缺省兼容 |
| 配置快照 | 修改静态配置不影响既有 EventHub，新实例使用新配置；外部列表修改不影响已保存配置或快照 |
| 生效时机 | StartThings 前建立观察及 Thing.Start 内主动辅助发布使用正确阈值 |
| 目录诊断 | 未匹配及非 Status/Config 目标记录 Warn；参数路径和末尾斜杠不自动修正；检查不调用 getter、不建立观察 |
| 累积变化 | 上升、下降、小变化累积、恰好达到阈值、未变化不发布 |
| 首次行为 | 自动观察初读不发布，主动首次有效调用发布 |
| 失败与生命周期 | 入队失败不更新基准、读取失败后恢复、关闭状态异常与返回行为保持兼容 |
| 通道与并发 | 不同通道独立累积，同路径并发不会以同一旧基准重复发布 |
| 数值边界 | 所有支持数值类型、long/ulong 大整数、decimal 极值及小数、Nullable |
| 特殊浮点 | NaN/Infinity 全部转换、正负零、浮点表示边界及差值溢出 |
| 非数值与容器 | 字符串、enum 等按相等性；数值容器仍按内容比较；快照隔离不退化 |
| 入口范围 | 直接 Publish 不过滤，AutoDenyList 不阻止主动辅助函数，参数 getter 不开启观察 |
| 全部观察入口 | 本地 SubscribeResource、SubscribeResources、独立 AcquireResourceObservation、AutoObserveList、WS 精确及正则订阅触发的观察均应用阈值 |
| 共享观察 | 多入口观察同一资源共享观察及基准，释放一个入口不影响其他有效需求 |
| 分发回归 | WS 集成验证阈值以下事件未分发，累积达到阈值的事件正常收到，无需新增协议字段 |

每个观察入口均须验证：初读不发布、小变化不发布且不更新基准、累积达到阈值才产生事件。主动辅助函数须验证相同配置优先级和通道基准隔离，直接 Publish 须验证不受过滤。整数和 decimal 验证精确差值与实际 double 阈值的比较，包括阈值超过 decimal 范围的情况。

功能完成证据必须包含实际命令、通过／失败结果、覆盖的入口及 netstandard2.0 构建、Core 和 WS 全量回归结果；不能仅以比较函数单测通过作为完成依据。本次实现的证据见第 12 节。

## 11. 提案结论

单资源配置采用 GlobalConfig 本地基础路径覆盖；全局、覆盖及有效阈值统一使用有限非负 double。EventHub 创建时捕获同一不可变配置快照，自动观察与主动辅助函数共同使用；比较接口新增 double 阈值重载并保留旧方法。整数和 decimal 差值与 double 阈值使用精确整数比比较，float/double 遵循浮点精度，特殊值按状态变化处理。配置共用不合并基准，直接 Publish 保持原行为。验收必须覆盖所有 Core 观察入口及 WS 分发。

本文件保留设计依据并记录当前实现。新增字段已接入配置解析和两个发布入口；加载本次代码后，在创建 Core 前配置即可生效。

## 12. 实现与验收记录（2026-10-12）

### 12.1 实现落点

- `Config/ResourceMinDeltaOverride.cs`：公共不可变配置项、列表复制及内部配置快照。
- `Config/GlobalConfig.MinDelta.cs`：代码配置入口、阈值及覆盖列表解析；GlobalConfig 的 Get/Set/Populate 已接入。
- `Event/NumericDeltaComparison.cs`：浮点差值和整数／decimal 差值与 double 阈值的整数比比较。
- `Event/ResourceValueComparison.cs`：保留旧方法，新增 double 阈值重载。
- `Event/EventHub.MinDelta.cs`：有效阈值查询及一次目录诊断；EventHub 构造捕获快照，StartThings 在 RootThing 注册后、Thing.Start 前诊断。
- 自动观察和主动辅助函数已调用同一比较重载及同一 EventHub 快照，保留各自基准与原入队更新时机。
- [开发示例](minDelta使用示例.md) 已补充。

### 12.2 验收覆盖

| 要求 | 实际证据 |
|---|---|
| 本地全部观察入口、初读及累计变化 | `MinDeltaTests.AllLocalObservationEntrypointsAccumulateAgainstPublishedBaseline` 的 exact、combined、handle、resident 四组用例 |
| WS 精确／正则入口与真实分发 | 独立仓库 `websocketcommunicationmodule` 下 `TestCode/WebSocketMinDeltaTests.cs` 的两组网络集成用例，首个及后续收到值均为达到阈值的累计变化 |
| 共享观察释放及重新初始化 | `SharedObservationSurvivesReleaseAndReinitializesAfterLastRelease` |
| EventHub 创建快照、Start 前观察及 Start 内发布 | 四组本地入口在创建后修改静态配置；`StartPublicationUsesCreationSnapshotAndNewEventHubUsesNewConfig` |
| 配置优先级、大小写、列表隔离及解析异常 | `ConfigurationCopiesRoundTripsAndRejectsInvalidWithoutMutation`、`OverrideErrorsIdentifyIndexAndPreserveConfiguration` 及四组观察入口 |
| 数值极值、特殊浮点、容器、旧接口及并发 | `NumericExtremesAndSpecialValues`、`ScalarFamiliesZeroCompatibilityAndConcurrentActiveBaseline`；原 ChangePublication 和容器测试继续通过 |
| 通道隔离及直接发布 | `ActiveChannelsUseSnapshotOverrideAndDirectPublicationBypassesThreshold` |
| 目录检查无 getter／观察副作用、一次 Warn | `DirectoryDiagnosticsWarnOnceWithoutReadingOrStartingObservation` |
| 失败、关闭、类型、时间戳及既有行为兼容 | 新并发／关闭用例和原 ChangePublicationTests、观察生命周期及容器测试的全量回归 |

### 12.3 实际命令与结果

以下命令分别从对应仓库根目录执行。WS 项目需具备其要求的 Core 项目依赖；仅检出 newcfet2core 仓库不包含 WS 测试源码。

```powershell
# 在 newcfet2core 仓库根目录执行
dotnet build Code/CFET2Core/CFET2Core.csproj --no-restore -v quiet -p:WarningLevel=0
dotnet test Code/CodeTest/CodeTest.csproj --no-restore -v quiet -p:WarningLevel=0 --logger 'trx;LogFileName=MinDelta-Core.trx'
# 在独立仓库 websocketcommunicationmodule 根目录执行
dotnet test TestCode/TestCode.csproj --no-restore -v quiet -p:WarningLevel=0 --logger 'trx;LogFileName=MinDelta-WS.trx'
```

- Core 的 netstandard2.0 构建成功；BigInteger 代码无需新增包。
- Core 全量 276 项通过，其中新增 minDelta 专项 12 项。
- WS 全量 282 项通过，其中新增阈值网络集成 2 项。
- 两个仓库的 `git diff --check` 通过。
- 结果文件：newcfet2core 仓库下 `Code/CodeTest/TestResults/MinDelta-Core.trx`；独立仓库 websocketcommunicationmodule 下 `TestCode/TestResults/MinDelta-WS.trx`。这些文件由测试运行生成，不要求随仓库提交；可使用上述命令重新生成。
- 首轮 Core 全量的两项失败来自旧测试把 Get 字符串字典直接序列化为 JSON，新增阈值成为字符串。现已改成 JSON 数值，仍保留数值字符串被拒绝的专项验证。
- WS 构建报告 NU1900：NuGet 漏洞数据源不可达；未影响构建和测试。测试不代表已打包、部署或远端消费者收到每个事件。

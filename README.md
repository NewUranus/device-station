# DeviceStation：多设备并行作业框架

一台上位机同时连着很多台设备（产线治具上的 8～32 块板子、实验室里的一排仪器、一批待配置的网关……），
需要对每一台跑同一套作业：写参数、升级、校准、测试、老化、采集数据。
DeviceStation 让每台设备**独立运行、独立停止、独立出结果**，任何一台出问题都不影响其它设备。

作业由一个个步骤组成，框架负责并发调度、超时重试、进度、日志和结果记录；具体做什么由作业自己决定。
仓库里带了两个示例作业——**固件烧录**和**功能测试**——用来演示一个完整的产线场景，它们只是框架的用法示例，不是框架本身。

核心逻辑是纯 .NET 类库，界面用 Avalonia（跨平台，写法接近 WPF）。

> 自带模拟设备，开箱即可在 macOS / Windows / Linux 上演示 8 台设备并行，不需要任何硬件。

![8 台模拟设备并行运行示例作业：6 台通过、工位 3 被单独停止、工位 6 失败，互不影响](docs/screenshot.png)

## 要解决的问题

| 现场的实际情况 | 框架要做到 |
| --- | --- |
| 8 台设备同时跑作业，其中一台串口线松了 | 只有那一台报 FAIL，其它照常跑完 |
| 操作员发现某个工位放错设备，要中途拔掉 | 单独停止那一台，其它不受影响 |
| USB Hub 带宽、电源、治具数量有限 | 可配置的最大并发数，多出来的排队 |
| 偶发的通信错误、设备不响应 | 每一步可配置超时和重试；长步骤可以断点续做 |
| 每台设备要可追溯 | 结果按 SN 写入 SQLite：工位、步骤、测量值、是否通过、时间 |
| 多台设备高频上报进度 | 界面节流，不卡顿；不用阻塞弹窗打断操作员 |

## 技术栈

- **.NET 8**（`net8.0`），C# 12
- **DeviceStation.Core**：纯类库，不依赖任何 UI
  - `System.IO.Ports`（串口）、`TcpClient`（网口设备/串口服务器）
  - `System.Threading.Channels`（读循环、结果写入队列）
  - `Microsoft.Extensions.Logging`（带 DeviceId 作用域）
  - `Microsoft.Data.Sqlite`（结果库）
- **DeviceStation.App**：Avalonia 11 + Fluent 主题 + CommunityToolkit.Mvvm（源生成器的 `[ObservableProperty]` / `[RelayCommand]`），编译绑定
- **DeviceStation.Core.Tests**：xUnit，全部基于模拟通道，不需要硬件

## 运行

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project src/DeviceStation.App
```

可选参数：`--autostart`（启动后自动全部开始）、`--max-concurrency 4`（并发上限，界面上也能改）、`--data <目录>`（数据目录）、`--firmware <文件>`（示例烧录作业使用的固件文件，缺省使用内置的 512 KB 演示数据）。

首次运行会在数据目录（macOS 为 `~/Library/Application Support/DeviceStation`）下生成 8 个模拟工位的配置 `slots/S01.json … S08.json`，结果写到 `results.db`。

> 项目目标框架是 `net8.0`。`Directory.Build.props` 设置了 `RollForward=Major`，所以只装了更新运行时（例如 .NET 10）的机器也能直接运行和测试。

## 架构

```
DeviceStation.sln
├─ src/DeviceStation.Core          纯类库（可单元测试、可换 UI / 做成服务）
│  ├─ Channels/   IDeviceChannel、SerialChannel、TcpChannel、SimulatedChannel、StreamDeviceChannel（读循环）
│  ├─ Protocol/   AsciiLineClient（演示用文本行协议）
│  ├─ Devices/    DeviceSlot + DeviceSlotStore（每工位一个 JSON）、DeviceSession、DeviceSnapshot
│  ├─ Jobs/       框架：IJobStep、StepPolicy、JobStep、IJob、CompositeJob、StepRunner（超时/重试）、DeviceContext
│  │  └─ Examples/  示例作业：FlashJob + FirmwareImage（固件烧录）、TestJob（上下限测试）、
│  │                以及对接模拟设备的 DemoFlashJob / DemoTestJob
│  ├─ Execution/  DeviceManager（调度、并发上限、隔离）
│  └─ Results/    IResultStore、SqliteResultStore（后台批量写入）
├─ src/DeviceStation.App           Avalonia 桌面界面（设备卡片墙）
└─ tests/DeviceStation.Core.Tests  xUnit
```

`Jobs/` 下的框架部分与具体场景无关；`Jobs/Examples/` 里的作业只依赖这些公开抽象，自己写的作业与它们地位相同。

一次运行的数据流：

```
Start(id) ─► DeviceSession.TryBeginRun（新 CTS、状态=Queued）
          ─► SemaphoreSlim.WaitAsync（并发名额）
          ─► IChannelFactory.Create（本次运行独占的通道）
          ─► 依次执行 JobStep：StepRunner 负责每次尝试的超时、重试
                │ 进度 ─► DeviceSession ─► DeviceManager.SessionChanged ─► UI（节流）
                │ 结果 ─► IResultStore.Add（入队，不阻塞）─► 后台线程批量写 SQLite
          ─► finally：关闭通道、归还名额 ─► 状态切到 Passed / Failed / Cancelled
```

### 框架的关键抽象

- **`IDeviceChannel`**：`OpenAsync / WriteAsync / ReadAsync`，全部带 `CancellationToken`。每次运行创建一个新实例，用完释放。
- **`IJobStep`**：`Name` + `ExecuteAsync(DeviceContext, CancellationToken)`。`JobStep` 给它配上 `StepPolicy`（超时、重试次数、重试间隔）、进度权重，以及是否算“连接”阶段。
- **`IJob` / `Job` / `CompositeJob`**：作业就是一串步骤；多个作业可以首尾串联成一个。
- **`DeviceContext`**：步骤拿到的一切：通道、带 DeviceId 作用域的日志、SN、`ReportStepProgress`、`Record`（写结果）、`GetOrAdd<T>`（本次运行内的状态）。
- **`DeviceManager`**：`StartAll / StopAll / Start(id) / Stop(id)`；`SessionChanged` 事件或 `IProgress<DeviceSnapshot>` 上报状态。
- **`SimulatedChannel`**：一个“真的会存数据”的模拟设备：写入的数据会被保存，`VERIFY` 时真算 CRC；支持随机延时、随机错误、随机不响应、测量超限，以及确定性的故障注入（`FailCommands`、`HangCommands`、`TransientFailures`）。

### 示例作业

- **`FlashJob`（固件烧录）**：连接 → 进 Bootloader → 擦除 → 分块写入 → 校验 → 重启。编排、超时、断点续写、进度都写好了，具体协议是 5 个抽象方法（带 TODO）。
- **`TestJob`（功能测试）**：测试项列表，每项一步，按 `[Low, High]` 判定，超限可按策略复测，每次测量都写记录。
- 演示程序把两者串成一个作业在 8 台模拟设备上运行。

## 难点与做法

**1. 隔离：一台出事不能连累其它台**
每台设备一个独立的运行任务、独立的 `CancellationTokenSource`、独立的通道实例。`DeviceManager.RunAsync` 把一次运行的所有异常（包括作业之外的，比如配置错误）都收敛成该设备自己的 `Failed` 状态，返回的任务永远不抛异常，所以 `Task.WhenAll` 不会因为一台失败而提前结束。停止也只取消自己的 CTS。

**2. 区分“超时”和“用户停止”**
每次尝试都创建一个与设备 CTS 链接的 CTS 并 `CancelAfter(timeout)`。捕获 `OperationCanceledException` 时看是哪一个被取消：设备 CTS → 用户停止，直接向上抛，不重试；只有尝试 CTS → 超时，转成 `TimeoutException` 进入重试。

**3. 串口读取不响应取消**
`SerialPort.BaseStream.ReadAsync` 在部分平台上忽略 `CancellationToken`，直接 await 会让超时失效。`StreamDeviceChannel` 打开后起一个后台读循环，把数据放进 `Channel<byte[]>`；业务代码只等这个队列（总能取消）。关闭通道时关掉底层流，读循环随之退出。

**4. 作业对象共享，设备状态隔离**
一个作业实例同时服务所有设备（示例里固件只加载一次，以 `ReadOnlyMemory<byte>` 共享，各设备只做 `Slice`，不复制）。因此作业对象本身不能存任何“某台设备”的状态，这些都放在 `DeviceContext.GetOrAdd<T>()` 里。

**5. 长步骤重试时断点续做**
示例烧录作业的写入游标存在每台设备自己的 `DeviceContext` 里，重试时从上次成功的块继续，不从头写；重新擦除会把游标归零。模拟设备在 `VERIFY` 时真的计算 CRC，所以续写错位会被测试发现（`WriteRetry_ResumesFromLastGoodBlock`）。同样的做法适用于任何可分段的长步骤。

**6. 并发上限与排队**
`SemaphoreSlim` 限制同时连接/运行的数量。在信号量上等待时状态是 `Queued`，排队中也能停止，且不会创建通道。

**7. 结果落盘不拖慢设备**
设备线程只把 `ResultRecord` 放进队列；`SqliteResultStore` 的单个后台线程独占连接，按批次在事务里写入（WAL 模式）。写库失败只记日志，不影响设备运行。`FlushAsync` 用一个放进同一队列的标记来等待之前的记录落盘。

**8. 界面节流**
设备线程触发事件时，ViewModel 只把最新快照写进 `ConcurrentDictionary`（同一台设备的旧快照被覆盖），UI 线程上的 `DispatcherTimer` 每 100 ms 应用一次。良率统计按 `RunId` 计数：节流会丢中间状态，但终态一定是一次运行的最后一个快照，所以不会漏也不会重。日志同样先进无锁队列，再由定时器批量显示；错误显示在卡片、日志和状态栏里，不弹阻塞对话框。

**9. 先释放资源，再报告完成**
运行结束时先关闭通道、归还并发名额，最后才把状态切到终态。这样操作员看到“PASS”后立刻点“开始”，串口已经释放，不会出现“端口被占用”。

## 测试

`tests/DeviceStation.Core.Tests` 全部使用 `SimulatedChannel`，覆盖：

- 8 台并行运行示例作业全部通过，SN 各不相同，结果带 SN
- 并发上限：峰值并发恰好等于上限，多出的处于排队状态
- 停止单台：被停的那台为 `Cancelled` 且通道已关闭，其它全部 `Passed`
- 单台失败隔离：一台在某一步必定失败，其它照常通过；作业之外的异常同样隔离
- 重试：瞬时故障在重试次数内恢复则通过，超过则失败；长步骤重试从断点续做且校验通过
- 超时：设备不响应时判为失败（不是取消），且在限定时间内结束
- 排队中停止：直接取消，不创建通道
- 测量超限判 FAIL，首测和复测都有带测量值的记录
- 完成后可再次开始；运行中重复开始返回同一次运行；全部停止
- 工位 JSON 多态序列化往返；SQLite 并行写入后按 SN 可查

## 用在自己的场景

1. 写自己的作业：实现 `IJobStep`（或直接用 `DelegateStep`），组成 `Job`，给每步配 `StepPolicy`；需要“连接”阶段时复用 `ConnectStep`。也可以参考 `Jobs/Examples` 里的两个示例继承扩展。
2. 在 `App.axaml.cs` 里把示例作业换成自己的 `IJob`。
3. 把工位 JSON 的 `channel` 改成真实的串口或 TCP：

```json
{ "id": "S01", "name": "工位 1", "channel": { "kind": "serial", "portName": "/dev/tty.usbserial-1410", "baudRate": 115200 } }
```

## 后续可做

- 工位配置编辑界面、扫码枪录入 SN
- 作业选择（同一套工位切换不同作业）
- 结果导出（CSV / MES 上传）、按 SN 查看历史

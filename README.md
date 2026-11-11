# DeviceStation

[![CI](https://github.com/NewUranus/device-station/actions/workflows/ci.yml/badge.svg)](https://github.com/NewUranus/device-station/actions/workflows/ci.yml)

多设备并行作业框架：一台上位机同时对很多台设备跑同一套作业，每台**独立运行、独立停止、独立出结果**，一台出问题不影响其它台。
仓库自带两个示例作业（固件烧录、功能测试）和模拟设备，不接硬件也能直接演示。

![8 台模拟设备并行：6 台通过、工位 3 被单独停止、工位 6 失败](docs/screenshot.png)

## 特性

- **步骤式作业**：每步可配超时和重试，长步骤重试时从断点续做
- **隔离**：每台设备独立的任务、`CancellationTokenSource` 和通道；失败、超时、停止都只影响自己
- **并发上限**：`SemaphoreSlim` 控制同时运行的台数，其余排队（排队中也能停止）
- **通道**：串口、TCP、模拟设备，全部可取消
- **日志落盘**：按设备、按天写文件 `logs/{DeviceId}/{yyyy-MM-dd}.log`，每行带 RunId（与结果库 `run_id` 前 8 位一致），保留 30 天
- **结果不丢**：结果按 SN 写入 SQLite（后台批量写，不阻塞设备）；写失败先重试，仍失败就转存到 `results.db.pending.jsonl`，下次启动自动补写
- **界面**：Avalonia 卡片墙，分段步骤轨显示每台走到哪一步；100 ms 节流刷新，无阻塞弹窗
- **按设备看日志**：点击设备卡片，日志面板只显示这台设备（每台单独保留最近 500 行，不会被别的设备刷掉），可再叠加级别筛选；没在看的设备出错时卡片亮红点

## 运行

```bash
dotnet run --project src/DeviceStation.App -- --autostart
```

```bash
dotnet test
```

参数：`--autostart` 启动即全部开始，`--max-concurrency <n>` 并发上限，`--data <目录>` 数据目录，`--firmware <文件>` 示例烧录用的固件。

数据目录（默认在用户的本地应用数据目录下的 `DeviceStation/`）：

```
slots/{Id}.json                 工位配置
results.db                      测试结果（SQLite）
results.db.pending.jsonl        数据库写不进去时暂存的结果（下次启动补写后删除）
logs/{DeviceId}/{日期}.log       每台设备的日志；logs/_app/ 是启动、结果库等非设备日志
```

目标框架 `net8.0`，设置了 `RollForward=Major`，只装 .NET 9/10 运行时也能跑。

## 结构

```
src/DeviceStation.Core     纯类库，不依赖 UI
  Channels/   IDeviceChannel：Serial / Tcp / Simulated
  Devices/    工位配置（每工位一个 JSON）、DeviceSession、快照
  Jobs/       IJobStep、StepPolicy、IJob、StepRunner（超时/重试）、DeviceContext
    Examples/ 示例作业：FlashJob（固件烧录）、TestJob（上下限测试）
  Execution/  DeviceManager：StartAll / StopAll / Start(id) / Stop(id)
  Results/    SqliteResultStore（重试 + 备用文件）
  Logging/    DeviceFileLoggerProvider（按设备、按天写日志文件）
src/DeviceStation.App      Avalonia 11 + CommunityToolkit.Mvvm
tests/DeviceStation.Core.Tests   xUnit，基于模拟设备
tests/DeviceStation.App.Tests    Avalonia Headless，在真实窗口里模拟点击
.github/workflows/ci.yml   每次推送在 Windows 和 macOS 上编译并跑全部测试
.superdesign/              界面设计稿（3 个方向，采用 station_1）
```

## 几个关键做法

- **超时 vs 停止**：每次尝试用一个链接到设备 CTS 的 CTS 并 `CancelAfter`，据此区分“超时→重试”和“用户停止→立即结束”。
- **串口读不响应取消**：后台读循环把数据放进 `Channel<byte[]>`，业务只等这个队列，所以总能取消。
- **作业共享、状态隔离**：一个作业实例服务所有设备（示例固件只加载一次，`ReadOnlyMemory<byte>` 切片共享），每台设备的状态放在 `DeviceContext`。
- **先释放再报完成**：关闭通道、归还名额之后才切到终态，看到 PASS 立刻重开不会撞上端口占用。
- **重试前清接收缓冲**：命令超时后设备的迟到响应可能已经进了通道队列，重试前用 `DiscardInput()` 清掉，避免把它当成新命令的响应。
- **日志和结果都能追溯**：日志和结果库都在后台线程落盘，设备线程不等磁盘；结果库被占用或损坏时，记录先进备用文件，宁可晚写也不丢。
- **坏配置只影响自己**：某个工位 JSON 损坏或 Id 重复时跳过并记错误日志，其它工位照常加载。

## 接入自己的场景

实现 `IJobStep`（或用 `DelegateStep`）组成 `Job`，在 `App.axaml.cs` 里替换示例作业；把工位 JSON 的 `channel` 改成真实设备：

```json
{ "id": "S01", "name": "工位 1", "channel": { "kind": "serial", "portName": "/dev/tty.usbserial-1410", "baudRate": 115200 } }
```

// 三个设计方向共用的演示数据（与 App 的真实状态模型一致）
window.DS = {
  job: "示例作业：固件烧录 + 功能测试",
  // 作业步骤及进度权重（与 FlashJob / TestJob 一致）
  steps: [
    ["连接", 1], ["进入 Bootloader", 1], ["擦除", 2], ["写入", 10], ["校验", 1], ["重启", 1],
    ["连接", 1], ["VBAT", 1], ["VCC_IO", 1], ["I_IDLE", 1], ["I_SLEEP", 1], ["OSC_FREQ", 1], ["TEMP", 1],
  ],
  kpi: { running: 4, queued: 1, passed: 27, failed: 3, stopped: 1, yield: "90.0%", concurrency: 6 },
  devices: [
    { id: "S01", name: "工位 1", state: "running", step: 3, frac: 0.62, detail: "320 / 512 KB", sn: "DS01-444898", time: "5.8 s", ok: 3, ng: 0 },
    { id: "S02", name: "工位 2", state: "passed", step: 13, frac: 1, detail: "", sn: "DS02-136177", time: "10.2 s", ok: 4, ng: 0, msg: "全部步骤通过" },
    { id: "S03", name: "工位 3", state: "stopped", step: 3, frac: 0.36, detail: "", sn: "DS03-727457", time: "4.1 s", ok: 2, ng: 0, msg: "用户停止" },
    { id: "S04", name: "工位 4", state: "running", step: 8, frac: 0.5, detail: "1.802 V", sn: "DS04-418736", time: "9.1 s", ok: 3, ng: 1 },
    { id: "S05", name: "工位 5", state: "queued", step: -1, frac: 0, detail: "", sn: "", time: "", ok: 4, ng: 0 },
    { id: "S06", name: "工位 6", state: "failed", step: 4, frac: 0, detail: "", sn: "DS06-701295", time: "8.4 s", ok: 1, ng: 2, msg: "校验失败（尝试 2 次）：CRC 不匹配" },
    { id: "S07", name: "工位 7", state: "connecting", step: 0, frac: 0, detail: "重试 1/2", sn: "", time: "0.9 s", ok: 4, ng: 0 },
    { id: "S08", name: "工位 8", state: "running", step: 2, frac: 0.4, detail: "", sn: "DS08-983854", time: "1.9 s", ok: 4, ng: 0 },
  ],
  log: [
    ["15:30:19.958", "INFO", "S04", "开始 VBAT 测量"],
    ["15:30:20.166", "WARN", "S08", "步骤 擦除 第 1/2 次失败：随机通信错误，200 ms 后重试"],
    ["15:30:20.410", "ERROR", "S06", "校验 失败（尝试 2 次）：VERIFY: CRC 不匹配"],
    ["15:30:21.157", "WARN", "S07", "步骤 连接 第 1/3 次失败：端口打开失败，200 ms 后重试"],
    ["15:30:22.791", "INFO", "S02", "通过，SN=DS02-136177，用时 10.2 s"],
    ["15:30:23.004", "INFO", "S03", "已停止（用户停止）"],
  ],
  label: { idle: "空闲", queued: "排队", connecting: "连接中", running: "运行中", passed: "PASS", failed: "FAIL", stopped: "已停止" },
};

// 当前步骤名（排队/完成时给出替代文字）
DS.stepName = d => d.state === "queued" ? "等待空闲名额" : d.state === "passed" ? "完成" : DS.steps[d.step][0];

// 整体进度 0~1（按步骤权重）
DS.progress = d => {
  if (d.state === "passed") return 1;
  if (d.step < 0) return 0;
  const total = DS.steps.reduce((s, x) => s + x[1], 0);
  const done = DS.steps.slice(0, d.step).reduce((s, x) => s + x[1], 0);
  return (done + DS.steps[d.step][1] * d.frac) / total;
};

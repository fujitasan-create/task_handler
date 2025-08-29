using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;    // ← 追加
using System.Timers;
using System.Windows;           // Dispatcher 用（WPF）
using LibreHardwareMonitor.Hardware;

namespace task_handler
{
    public class MetricsViewModel : INotifyPropertyChanged, IDisposable
    {
        // ==== 公開プロパティ ====
        private string _cpuUsageText = "-- %";
        public string CpuUsageText { get => _cpuUsageText; set { _cpuUsageText = value; OnPropertyChanged(); } }

        private string _memoryUsageText = "-- % (-- / -- GB)";
        public string MemoryUsageText { get => _memoryUsageText; set { _memoryUsageText = value; OnPropertyChanged(); } }

        private string _gpuUsageText = "-- %";
        public string GpuUsageText { get => _gpuUsageText; set { _gpuUsageText = value; OnPropertyChanged(); } }

        private string _pingText = "-- ms (avg)";
        public string PingText { get => _pingText; set { _pingText = value; OnPropertyChanged(); } }

        private string _temperatureText = "CPU: -- °C / GPU: -- °C";
        public string TemperatureText { get => _temperatureText; set { _temperatureText = value; OnPropertyChanged(); } }

        private float _cpuUsageValue;
        public float CpuUsageValue { get => _cpuUsageValue; set { _cpuUsageValue = value; OnPropertyChanged(); } }

        private float _memoryUsageValue;
        public float MemoryUsageValue { get => _memoryUsageValue; set { _memoryUsageValue = value; OnPropertyChanged(); } }

        private float _gpuUsageValue;
        public float GpuUsageValue { get => _gpuUsageValue; set { _gpuUsageValue = value; OnPropertyChanged(); } }

        // Topプロセス用
        public class ProcRow
        {
            public string Name { get; set; } = "";
            public int Pid { get; set; }
            public float CpuPct { get; set; }
            public float MemMB { get; set; }
            public float GpuPct { get; set; }
        }
        public ObservableCollection<ProcRow> TopProcs { get; } = new();

        // ==== 内部フィールド ====
        private readonly PerformanceCounter _cpuTotal =
            new("Processor", "% Processor Time", "_Total", true);

        private readonly System.Timers.Timer _timer;
        private readonly Ping _ping = new();
        private readonly Queue<long> _lat = new();
        private readonly int _latWindow = 10;
        private const int TOP_ROWS = 30;

        // GPU（合計）
        private readonly List<PerformanceCounter> _gpuCounters = new();

        // Topプロセス計測
        private readonly Dictionary<int, (TimeSpan cpuTime, DateTime ts)> _prevCpu = new();
        private readonly int _logicalProcessorCount = Environment.ProcessorCount;

        // GPU（プロセス別）
        private readonly List<PerformanceCounter> _gpuPerEngine = new();
        private readonly Regex _pidRegex = new(@"pid_(\d+)", RegexOptions.Compiled);

        // 再入防止＆間引き
        private readonly SemaphoreSlim _tickGate = new(1, 1);
        private int _tickCount = 0;

        // GPUのPID別集計スイッチ（重い環境は false 推奨）
        private const bool ENABLE_GPU_PER_PROCESS = false;

        // ==== 温度（LibreHardwareMonitor） ====
        private Computer? _pc;                 // LHM本体
        private ISensor? _cpuTempSensor;       // 代表CPU温度
        private ISensor? _gpuTempSensor;       // 代表GPU温度
        private DateTime _lastTempScan = DateTime.MinValue;

        public MetricsViewModel()
        {
            // CPUカウンタのウォームアップ
            _ = _cpuTotal.NextValue();

            // GPUカウンタ列挙（3D合算）
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (!inst.Contains("engtype_3D")) continue;
                    try
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                        _gpuCounters.Add(pc);
                        _ = pc.NextValue();
                    }
                    catch { }
                }
            }
            catch { /* 環境によっては存在しない */ }

            // ★ 温度：LHM初期化（EC/コントローラも有効化）
            try
            {
                _pc = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true,   // ← 追加：EC経由の温度対策
                    IsMemoryEnabled = true        // （お好み。将来の拡張用）
                };
                _pc.Open();
                RefreshTempSensors(); // 一度センサーを見つけておく
            }
            catch { /* 失敗したら温度は "--" 表示のまま */ }

            // タイマー
            _timer = new System.Timers.Timer(1500);
            _timer.Elapsed += async (_, __) => await OnTimerAsync();
            _timer.AutoReset = true;
            _timer.Start();
        }

        // ==== タイマー処理（再入防止＋間引き）====
        private async Task OnTimerAsync()
        {
            if (!await _tickGate.WaitAsync(0)) return;
            try
            {
                _tickCount++;

                // 軽い処理は毎回
                UpdateCpu();
                UpdateMemory();
                await UpdatePingAsync();

                // GPU合計は2回に1回
                if (_tickCount % 2 == 0)
                    UpdateGpu();

                // Topプロセスは3回に1回
                if (_tickCount % 3 == 0)
                {
                    if (ENABLE_GPU_PER_PROCESS) UpdateTopProcesses();
                    else UpdateTopProcesses_NoGpu();
                }

                // 温度は4回に1回（約6秒ごと）
                if (_tickCount % 4 == 0)
                    UpdateTemperatures();
            }
            finally
            {
                _tickGate.Release();
            }
        }

        // ==== 個別更新 ====
        private void UpdateCpu()
        {
            float v = _cpuTotal.NextValue();
            CpuUsageText = $"{v:F1} %";
            CpuUsageValue = v;
        }

        private void UpdateMemory()
        {
            var (usedGB, totalGB, pct) = GetMemory();
            MemoryUsageText = $"{pct:F1} %  ({usedGB:F1} / {totalGB:F1} GB)";
            MemoryUsageValue = (float)pct;
        }

        private void UpdateGpu()
        {
            if (_gpuCounters.Count == 0)
            {
                GpuUsageText = "N/A";
                GpuUsageValue = 0;
                return;
            }
            float sum = 0f;
            foreach (var pc in _gpuCounters)
            {
                try { sum += pc.NextValue(); } catch { }
            }
            sum = Math.Clamp(sum, 0f, 100f);
            GpuUsageText = $"{sum:F1} %";
            GpuUsageValue = sum;
        }

        // ==== Ping ====
        private async Task UpdatePingAsync()
        {
            try
            {
                var reply = await _ping.SendPingAsync("8.8.8.8", 2000);
                long ms = reply.Status == IPStatus.Success ? reply.RoundtripTime : 2000;
                _lat.Enqueue(ms);
                while (_lat.Count > _latWindow) _lat.Dequeue();
                PingText = $"{_lat.Average():F0} ms (avg)";
            }
            catch
            {
                PingText = "timeout";
            }
        }

        // ==== 温度ロジック ====
        private static bool IsSensibleTemp(float? v) => v.HasValue && v.Value > 5f && v.Value < 110f;

        // 全デバイス/サブデバイスを更新
        private void UpdateAllHardware()
        {
            if (_pc == null) return;
            foreach (var hw in _pc.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
            }
        }

        private static readonly string[] CpuTempPrefer =
            { "Package", "Tctl", "Tdie", "Core (Tctl/Tdie)", "Core Max", "CPU Die", "CCD", "CPU" };
        private static readonly string[] GpuTempPrefer =
            { "Hot Spot", "GPU Core", "GPU" };

        private ISensor? PickBestByNameThenMax(IEnumerable<ISensor> sensors, string[] prefer)
        {
            // 名前優先（妥当値）→ それ以外は妥当値の最大
            foreach (var kw in prefer)
            {
                var s = sensors.FirstOrDefault(x =>
                    x.SensorType == SensorType.Temperature &&
                    (x.Name?.IndexOf(kw, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 &&
                    IsSensibleTemp(x.Value));
                if (s != null) return s;
            }
            return sensors
                .Where(x => x.SensorType == SensorType.Temperature && IsSensibleTemp(x.Value))
                .OrderByDescending(x => x.Value ?? float.MinValue)
                .FirstOrDefault();
        }

        private void RefreshTempSensors()
        {
            if (_pc == null) return;

            UpdateAllHardware();

            // CPU候補: (1) CPU配下 (2) マザボ(SuperIO/EC)配下の「CPU/Package/Tctl/Tdie」系
            var cpuCandidates = new List<ISensor>();

            foreach (var cpuHw in _pc.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
                cpuCandidates.AddRange(cpuHw.Sensors);

            var mb = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
            if (mb != null)
            {
                foreach (var sub in mb.SubHardware)
                {
                    sub.Update();
                    foreach (var s in sub.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature) continue;
                        var n = s.Name ?? "";
                        if (n.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            cpuCandidates.Add(s);
                        }
                    }
                }
            }

            // 最後の手段：マザボ温度の中で妥当な最大（CPUらしき温度が1つも見つからない場合）
            if (cpuCandidates.Count == 0 && mb != null)
            {
                var allMbTemps = new List<ISensor>();
                foreach (var sub in mb.SubHardware)
                {
                    sub.Update();
                    allMbTemps.AddRange(sub.Sensors.Where(s => s.SensorType == SensorType.Temperature));
                }
                var fallback = allMbTemps.Where(s => IsSensibleTemp(s.Value))
                                         .OrderByDescending(s => s.Value ?? float.MinValue)
                                         .FirstOrDefault();
                if (fallback != null) cpuCandidates.Add(fallback);
            }

            _cpuTempSensor = PickBestByNameThenMax(cpuCandidates, CpuTempPrefer);

            // GPU候補: GPUデバイス配下
            var gpuCandidates = new List<ISensor>();
            foreach (var gpuHw in _pc.Hardware.Where(h =>
                h.HardwareType == HardwareType.GpuNvidia ||
                h.HardwareType == HardwareType.GpuAmd ||
                h.HardwareType == HardwareType.GpuIntel))
            {
                gpuCandidates.AddRange(gpuHw.Sensors);
            }
            _gpuTempSensor = PickBestByNameThenMax(gpuCandidates, GpuTempPrefer);

            _lastTempScan = DateTime.UtcNow;
        }

        private static float? ReadSensorTemp(ISensor? s)
        {
            if (s == null) return null;
            try
            {
                s.Hardware.Update();
                foreach (var sub in s.Hardware.SubHardware) sub.Update();
                var v = s.Value;
                return IsSensibleTemp(v) ? v : (float?)null;
            }
            catch { return null; }
        }

        private void UpdateTemperatures()
        {
            if (_pc == null) return;

            // スリープ復帰やGPU切替対策：30秒に1回はセンサーを取り直し
            if ((DateTime.UtcNow - _lastTempScan).TotalSeconds > 30)
                RefreshTempSensors();

            UpdateAllHardware();

            var cpu = ReadSensorTemp(_cpuTempSensor);
            if (!cpu.HasValue) { RefreshTempSensors(); cpu = ReadSensorTemp(_cpuTempSensor); }

            var gpu = ReadSensorTemp(_gpuTempSensor);

            TemperatureText =
                $"CPU: {(cpu?.ToString("F0") ?? "--")} °C / GPU: {(gpu?.ToString("F0") ?? "--")} °C";
        }

        // ==== Topプロセス（GPUあり）====
        private void EnsureGpuPerEngineCounters()
        {
            if (_gpuPerEngine.Count > 0) return;
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (!inst.Contains("engtype_3D")) continue;
                    try
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                        _gpuPerEngine.Add(pc);
                        _ = pc.NextValue();
                    }
                    catch { }
                }
                if (_gpuPerEngine.Count > 200)
                    _gpuPerEngine.Clear(); // 安全弁：多すぎる環境は諦める
            }
            catch { }
        }

        private void UpdateTopProcesses()
        {
            try
            {
                EnsureGpuPerEngineCounters();

                var gpuByPid = new Dictionary<int, float>();
                foreach (var pc in _gpuPerEngine)
                {
                    try
                    {
                        float v = pc.NextValue();
                        var m = _pidRegex.Match(pc.InstanceName);
                        if (!m.Success) continue;
                        int pid = int.Parse(m.Groups[1].Value);
                        if (!gpuByPid.ContainsKey(pid)) gpuByPid[pid] = 0f;
                        gpuByPid[pid] += v;
                    }
                    catch { }
                }

                var now = DateTime.UtcNow;
                var procs = Process.GetProcesses();
                var rows = new List<ProcRow>(procs.Length);

                foreach (var p in procs)
                {
                    try
                    {
                        var cpu = p.TotalProcessorTime;
                        float cpuPct = 0f;
                        if (_prevCpu.TryGetValue(p.Id, out var prev))
                        {
                            var dt = (float)(now - prev.ts).TotalSeconds;
                            if (dt > 0.2f)
                            {
                                var dcpu = (float)(cpu - prev.cpuTime).TotalSeconds;
                                cpuPct = Math.Clamp((dcpu / dt) * 100f / _logicalProcessorCount, 0f, 1000f);
                            }
                        }
                        _prevCpu[p.Id] = (cpu, now);

                        float memMB = p.WorkingSet64 / (1024f * 1024f);
                        gpuByPid.TryGetValue(p.Id, out float gpuPct);

                        rows.Add(new ProcRow
                        {
                            Name = string.IsNullOrEmpty(p.ProcessName) ? "(unknown)" : p.ProcessName,
                            Pid = p.Id,
                            CpuPct = cpuPct,
                            MemMB = memMB,
                            GpuPct = gpuPct
                        });
                    }
                    catch { }
                }

                var top = rows
                    .OrderByDescending(r => r.CpuPct)
                    .ThenByDescending(r => r.GpuPct)
                    .ThenByDescending(r => r.MemMB)
                    .Take(TOP_ROWS)
                    .ToList();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    TopProcs.Clear();
                    foreach (var r in top) TopProcs.Add(r);
                });

                var alive = new HashSet<int>(procs.Select(p => p.Id));
                var dead = _prevCpu.Keys.Where(pid => !alive.Contains(pid)).ToList();
                foreach (var d in dead) _prevCpu.Remove(d);
            }
            catch { }
        }

        // ==== Topプロセス（GPUなし軽量版）====
        private void UpdateTopProcesses_NoGpu()
        {
            try
            {
                var now = DateTime.UtcNow;
                var procs = Process.GetProcesses();
                var rows = new List<ProcRow>(procs.Length);

                foreach (var p in procs)
                {
                    try
                    {
                        var cpu = p.TotalProcessorTime;
                        float cpuPct = 0f;
                        if (_prevCpu.TryGetValue(p.Id, out var prev))
                        {
                            var dt = (float)(now - prev.ts).TotalSeconds;
                            if (dt > 0.2f)
                            {
                                var dcpu = (float)(cpu - prev.cpuTime).TotalSeconds;
                                cpuPct = Math.Clamp((dcpu / dt) * 100f / _logicalProcessorCount, 0f, 1000f);
                            }
                        }
                        _prevCpu[p.Id] = (cpu, now);

                        float memMB = p.WorkingSet64 / (1024f * 1024f);

                        rows.Add(new ProcRow
                        {
                            Name = string.IsNullOrEmpty(p.ProcessName) ? "(unknown)" : p.ProcessName,
                            Pid = p.Id,
                            CpuPct = cpuPct,
                            MemMB = memMB,
                            GpuPct = 0f
                        });
                    }
                    catch { }
                }

                var top = rows
                    .OrderByDescending(r => r.CpuPct)
                    .ThenByDescending(r => r.MemMB)
                    .Take(TOP_ROWS)
                    .ToList();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    TopProcs.Clear();
                    foreach (var r in top) TopProcs.Add(r);
                });

                var alive = new HashSet<int>(procs.Select(p => p.Id));
                var dead = _prevCpu.Keys.Where(pid => !alive.Contains(pid)).ToList();
                foreach (var d in dead) _prevCpu.Remove(d);
            }
            catch { }
        }

        // ==== メモリ ====
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private static (double usedGB, double totalGB, double percent) GetMemory()
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (!GlobalMemoryStatusEx(ref mem)) return (double.NaN, double.NaN, double.NaN);
            double total = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
            double avail = mem.ullAvailPhys / 1024.0 / 1024 / 1024;
            double used = total - avail;
            double pct = (used / total) * 100.0;
            return (used, total, pct);
        }

        // ==== INotifyPropertyChanged ====
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ==== IDisposable ====
        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _cpuTotal?.Dispose();
            foreach (var pc in _gpuCounters) pc.Dispose();
            foreach (var pc in _gpuPerEngine) pc.Dispose();
            _ping?.Dispose();

            try { _pc?.Close(); } catch { }
        }
    }
}

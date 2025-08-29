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
using System.Timers;
using System.Windows; // Dispatcher 用（WPF）

namespace task_handler
{
    public class MetricsViewModel : INotifyPropertyChanged, IDisposable
    {
        // ==== 公開用プロパティ（XAMLバインド用）====
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

        // Topプロセス表示用
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

        // GPU（合計用）
        private readonly List<PerformanceCounter> _gpuCounters = new();

        // Topプロセス計測
        private readonly Dictionary<int, (TimeSpan cpuTime, DateTime ts)> _prevCpu = new();
        private readonly int _logicalProcessorCount = Environment.ProcessorCount;

        // GPU（プロセス別用）
        private readonly List<PerformanceCounter> _gpuPerEngine = new();
        private readonly Regex _pidRegex = new(@"pid_(\d+)", RegexOptions.Compiled);

        // 再入防止＆間引き
        private readonly SemaphoreSlim _tickGate = new(1, 1);
        private int _tickCount = 0;

        // GPUのPID別集計スイッチ（重い環境は false 推奨）
        private const bool ENABLE_GPU_PER_PROCESS = false;

        // ==== ctor ====
        public MetricsViewModel()
        {
            // CPUカウンタは初回0%になりがち → ウォームアップ
            _ = _cpuTotal.NextValue();

            // GPUカウンタ列挙（3Dエンジン合計）
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
                        _ = pc.NextValue(); // ウォームアップ
                    }
                    catch { }
                }
            }
            catch { /* 環境によっては存在しない */ }

            // タイマー（1.5秒ごと／AutoReset）
            _timer = new System.Timers.Timer(1500);
            _timer.Elapsed += async (_, __) => await OnTimerAsync();
            _timer.AutoReset = true;
            _timer.Start();
        }

        // ==== タイマー処理（再入防止＋間引き）====
        private async Task OnTimerAsync()
        {
            if (!await _tickGate.WaitAsync(0)) return; // 処理中ならスキップ
            try
            {
                _tickCount++;

                // 軽い処理は毎回
                UpdateCpu();
                UpdateMemory();
                await UpdatePingAsync();

                // GPUトータルは2回に1回
                if (_tickCount % 2 == 0)
                    UpdateGpu();

                // Topプロセスは3回に1回
                if (_tickCount % 3 == 0)
                {
                    if (ENABLE_GPU_PER_PROCESS) UpdateTopProcesses();
                    else UpdateTopProcesses_NoGpu();
                }
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
            sum = Math.Clamp(sum, 0f, 100f); // クリップ
            GpuUsageText = $"{sum:F1} %";
            GpuUsageValue = sum;
        }

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

        private void UpdateTemperatures()
        {
            // LibreHardwareMonitor 導入後に実装する想定
            // TemperatureText = $"CPU: {cpuTemp:F0} °C / GPU: {gpuTemp:F0} °C";
        }

        // ==== Topプロセス（GPU あり）====
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

                // 安全弁：多すぎると重いので諦める
                if (_gpuPerEngine.Count > 200)
                    _gpuPerEngine.Clear();
            }
            catch { }
        }

        private void UpdateTopProcesses()
        {
            try
            {
                EnsureGpuPerEngineCounters();

                // GPU% を pid ごとに合算
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
                        // CPU%
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

                        // Mem(MB)
                        float memMB = p.WorkingSet64 / (1024f * 1024f);

                        // GPU%
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
                    .Take(5)
                    .ToList();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    TopProcs.Clear();
                    foreach (var r in top) TopProcs.Add(r);
                });

                // 終了したPIDのキャッシュ掃除
                var alive = new HashSet<int>(procs.Select(p => p.Id));
                var dead = _prevCpu.Keys.Where(pid => !alive.Contains(pid)).ToList();
                foreach (var d in dead) _prevCpu.Remove(d);
            }
            catch { }
        }

        // ==== Topプロセス（GPU なしの軽量版）====
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
                    .Take(5)
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

        // ==== メモリ（GlobalMemoryStatusEx）====
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
        }
    }
}

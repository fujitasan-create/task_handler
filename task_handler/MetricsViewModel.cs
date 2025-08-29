using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Timers;
using System.Diagnostics;

namespace task_handler
{
    public class MetricsViewModel : INotifyPropertyChanged, IDisposable
    {
        // ==== 公開用プロパティ（XAMLとバインド）====
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

        // ==== 内部 ====
        private readonly PerformanceCounter _cpuTotal =
            new("Processor", "% Processor Time", "_Total", true);

        private readonly System.Timers.Timer _timer;            
        private readonly Ping _ping = new();
        private readonly Queue<long> _lat = new();
        private readonly int _latWindow = 10;

        // GPU Engine カウンタキャッシュ
        private readonly List<PerformanceCounter> _gpuCounters = new();

        // LibreHardwareMonitor を使うなら後で初期化（NuGet入れた後で）
        // private readonly LibreHardwareMonitor.Hardware.Computer _pc;

        public MetricsViewModel()
        {
            // CPUカウンタは最初の1回は0%になりがち → ウォームアップ
            _ = _cpuTotal.NextValue();

            // GPUカウンタ列挙(3Dエンジン合算)
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
                    catch { /* 無視 */ }
                }
            }
            catch
            {
                // Windowsやドライバによって存在しない場合あり
            }

            _timer = new System.Timers.Timer(1000);
            _timer.Elapsed += async (_, __) => await TickAsync();
            _timer.AutoReset = true;
            _timer.Start();

            // ▼ 温度を入れるなら（あとでNuGet導入後にコメント解除）
            // _pc = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            // _pc.Open();
        }

        private async Task TickAsync()
        {
            try
            {
                UpdateCpu();
                UpdateMemory();
                UpdateGpu();
                await UpdatePingAsync();
                UpdateTemperatures(); // LibreHardwareMonitor 導入後に実装
            }
            catch
            {
                // ここでUIが固まらないように握りつぶし
            }
        }

        private void UpdateCpu()
        {
            float v = _cpuTotal.NextValue();
            CpuUsageText = $"{v:F1} %";
        }

        private void UpdateMemory()
        {
            var (usedGB, totalGB, pct) = GetMemory();
            MemoryUsageText = $"{pct:F1} %  ({usedGB:F1} / {totalGB:F1} GB)";
        }

        private void UpdateGpu()
        {
            if (_gpuCounters.Count == 0)
            {
                GpuUsageText = "N/A";
                return;
            }
            float sum = 0f;
            foreach (var pc in _gpuCounters)
            {
                try { sum += pc.NextValue(); } catch { }
            }
            // 複数エンジン合計。100%を超える場合があるのでクリップ
            sum = Math.Clamp(sum, 0f, 100f);
            GpuUsageText = $"{sum:F1} %";
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
            // ひとまずダミー。LibreHardwareMonitor を入れたら実装に置き換え。
            // TemperatureText = $"CPU: {cpuTemp:F0} °C / GPU: {gpuTemp:F0} °C";
        }

        // ===== メモリ（GlobalMemoryStatusEx）=====
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

        // ===== INotifyPropertyChanged =====
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _cpuTotal?.Dispose();
            foreach (var pc in _gpuCounters) pc.Dispose();
            _ping?.Dispose();

            // _pc?.Close();
        }
    }
}

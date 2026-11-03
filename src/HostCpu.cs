using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace AdaptiveNet.Core
{
    /// <summary>Contadores acumulados de CPU do sistema, nas unidades do SO (ticks de /proc/stat ou 100 ns do Windows).</summary>
    internal readonly struct CpuTimes
    {
        public CpuTimes(ulong total, ulong idle, long steal)
        {
            Total = total;
            Idle = idle;
            Steal = steal;
        }

        /// <summary>Tempo total de todos os nucleos (ocioso incluso).</summary>
        public ulong Total { get; }
        /// <summary>Ocioso (no Linux soma iowait: um nucleo esperando disco nao esta gastando CPU).</summary>
        public ulong Idle { get; }
        /// <summary>Tempo roubado pelo hipervisor, ou -1 quando o SO nao informa.</summary>
        public long Steal { get; }
    }

    /// <summary>Le a primeira linha ("cpu  ...") de /proc/stat. Funcao pura, testada com texto real.</summary>
    internal static class ProcStatParser
    {
        public static bool TryParse(string text, out CpuTimes times)
        {
            times = default;
            if (string.IsNullOrEmpty(text)) return false;
            int lineEnd = text.IndexOf('\n');
            string line = (lineEnd < 0 ? text : text.Substring(0, lineEnd)).Trim();
            string[] fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            // cpu user nice system idle [iowait irq softirq steal guest guest_nice]; kernels antigos so trazem 4.
            if (fields.Length < 5 || fields[0] != "cpu") return false;

            var values = new ulong[10];
            int count = Math.Min(10, fields.Length - 1);
            for (int index = 0; index < count; index++)
            {
                if (!ulong.TryParse(fields[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out values[index])) return false;
            }
            // guest e guest_nice (indices 8 e 9) ja estao dentro de user/nice: nao entram na soma.
            ulong user = values[0], nice = values[1], system = values[2], idle = values[3];
            ulong iowait = values[4], irq = values[5], softirq = values[6], steal = values[7];
            ulong total = user + nice + system + idle + iowait + irq + softirq + steal;
            times = new CpuTimes(total, idle + iowait, count >= 8 ? (long)steal : -1L);
            return true;
        }
    }

    /// <summary>Contas de delta entre duas leituras de <see cref="CpuTimes"/>.</summary>
    internal static class HostCpuMath
    {
        /// <summary>Uso do sistema em % entre duas leituras; -1 quando nao deu para medir (sem delta, contador voltou).</summary>
        public static double SystemPercent(CpuTimes previous, CpuTimes current)
        {
            if (current.Total <= previous.Total) return -1d;
            ulong total = current.Total - previous.Total;
            if (current.Idle < previous.Idle) return -1d;
            ulong idle = current.Idle - previous.Idle;
            if (idle > total) return -1d;
            return 100d * (total - idle) / total;
        }

        /// <summary>Steal em % do tempo total; -1 quando o SO nao informa (Windows, kernel antigo).</summary>
        public static double StealPercent(CpuTimes previous, CpuTimes current)
        {
            if (previous.Steal < 0L || current.Steal < 0L || current.Total <= previous.Total) return -1d;
            if (current.Steal < previous.Steal) return -1d;
            double percent = 100d * (current.Steal - previous.Steal) / (current.Total - previous.Total);
            return Math.Min(100d, percent);
        }
    }

    internal interface ICpuTimesReader
    {
        /// <summary>Falso quando a leitura nao esta disponivel agora; nunca lanca.</summary>
        bool TryRead(out CpuTimes times);
    }

    internal sealed class ProcStatCpuReader : ICpuTimesReader
    {
        private const string Path = "/proc/stat";

        public bool TryRead(out CpuTimes times)
        {
            times = default;
            try
            {
                using (var reader = new StreamReader(Path))
                {
                    return ProcStatParser.TryParse(reader.ReadLine(), out times);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    internal sealed class WindowsCpuReader : ICpuTimesReader
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        public bool TryRead(out CpuTimes times)
        {
            times = default;
            try
            {
                if (!GetSystemTimes(out long idle, out long kernel, out long user)) return false;
                // O tempo de kernel ja inclui o ocioso: total = kernel + user.
                times = new CpuTimes((ulong)Math.Max(0L, kernel) + (ulong)Math.Max(0L, user), (ulong)Math.Max(0L, idle), -1L);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Servidor: 1 amostra por tick (~1 s) da CPU do processo e do host. Qualquer falha vira
    /// "nao medido" e nunca chega ao jogo; depois de falhas seguidas a leitura se desliga.
    /// </summary>
    internal sealed class HostCpuProbe
    {
        private const int MaxConsecutiveFailures = 5;

        private readonly ICpuTimesReader _reader;
        private readonly Func<double> _processCpuMilliseconds;
        private bool _hasBaseline;
        private CpuTimes _lastTimes;
        private double _lastWall;
        private double _lastProcessCpuMs;
        private int _failures;
        private bool _disabled;

        public HostCpuProbe(ICpuTimesReader reader, Func<double> processCpuMilliseconds)
        {
            _reader = reader;
            _processCpuMilliseconds = processCpuMilliseconds;
        }

        public static HostCpuProbe CreateForThisMachine()
        {
            ICpuTimesReader reader = Environment.OSVersion.Platform == PlatformID.Unix
                ? (ICpuTimesReader)new ProcStatCpuReader()
                : new WindowsCpuReader();
            Process process = null;
            return new HostCpuProbe(reader, () =>
            {
                if (process == null) process = Process.GetCurrentProcess();
                process.Refresh();
                return process.TotalProcessorTime.TotalMilliseconds;
            });
        }

        public bool Disabled => _disabled;

        public HostCpuSample Sample(double nowSeconds)
        {
            if (_disabled) return default;
            try
            {
                double processCpuMs = _processCpuMilliseconds();
                bool read = _reader.TryRead(out CpuTimes times);
                double processPerSecond = _lastWall > 0d
                    ? CpuUsageMath.ProcessMsPerSecond(processCpuMs - _lastProcessCpuMs, nowSeconds - _lastWall)
                    : -1d;
                double systemPercent = read && _hasBaseline ? HostCpuMath.SystemPercent(_lastTimes, times) : -1d;
                double stealPercent = read && _hasBaseline ? HostCpuMath.StealPercent(_lastTimes, times) : -1d;

                _lastWall = nowSeconds;
                _lastProcessCpuMs = processCpuMs;
                if (read)
                {
                    _lastTimes = times;
                    _failures = 0;
                    _hasBaseline = true;
                }
                else if (++_failures >= MaxConsecutiveFailures)
                {
                    _disabled = true;
                }
                return new HostCpuSample(processPerSecond, systemPercent, stealPercent, Environment.ProcessorCount);
            }
            catch (Exception)
            {
                _disabled = true;
                return default;
            }
        }
    }

    /// <summary>Resumo da CPU do host para a linha de log de 15 s: media do processo, maximos do sistema e do steal.</summary>
    internal sealed class HostCpuLogWindow
    {
        private double _processSum;
        private int _processCount;
        private double _systemMax = -1d;
        private double _stealMax = -1d;

        public void Add(HostCpuSample sample)
        {
            if (!sample.Measured) return;
            if (sample.ProcessCpuMsPerSecond >= 0d)
            {
                _processSum += sample.ProcessCpuMsPerSecond;
                _processCount++;
            }
            _systemMax = Math.Max(_systemMax, sample.SystemCpuPercent);
            _stealMax = Math.Max(_stealMax, sample.StealPercent);
        }

        public void Reset()
        {
            _processSum = 0d;
            _processCount = 0;
            _systemMax = -1d;
            _stealMax = -1d;
        }

        /// <summary>"hostCpu=proc 850ms/s, sysMax 62%, stealMax 0.0%" ou "hostCpu=n/a" quando nada foi medido.</summary>
        public string Format()
        {
            if (_processCount == 0 && _systemMax < 0d) return "hostCpu=n/a";
            string process = _processCount > 0 ? (_processSum / _processCount).ToString("F0", CultureInfo.InvariantCulture) + "ms/s" : "n/a";
            string system = _systemMax >= 0d ? _systemMax.ToString("F0", CultureInfo.InvariantCulture) + "%" : "n/a";
            string steal = _stealMax >= 0d ? _stealMax.ToString("F1", CultureInfo.InvariantCulture) + "%" : "n/a";
            return $"hostCpu=proc {process}, sysMax {system}, stealMax {steal}";
        }
    }
}

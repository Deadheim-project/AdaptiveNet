using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AdaptiveNet
{
    /// <summary>
    /// Cliente: conta as IAs que ESTE processo instanciou (BaseAI.Instances) e quantas ele simula
    /// (ZNetView.IsOwner). Sao dezenas de objetos por segundo; o servidor cruza com o censo por setor.
    /// </summary>
    internal static class ClientSimulationProbe
    {
        private static bool _disabled;

        public static ClientAiSample Sample()
        {
            if (_disabled) return default;
            try
            {
                var instances = BaseAI.Instances;
                if (instances == null) return default;
                int total = 0;
                int owned = 0;
                for (int index = 0; index < instances.Count; index++)
                {
                    BaseAI ai = instances[index] as BaseAI;
                    if (ai == null) continue;
                    ZNetView view = ai.GetComponent<ZNetView>();
                    if (view == null || !view.IsValid()) continue;
                    total++;
                    if (view.IsOwner()) owned++;
                }
                return new ClientAiSample(total, owned, total - owned);
            }
            catch (Exception)
            {
                _disabled = true;
                return default;
            }
        }
    }

    /// <summary>CPU do processo, CPU do sistema (GetSystemTimes) e memoria do processo, a cada relatorio.</summary>
    internal static class ClientMachineProbe
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        private static Process _process;
        private static double _lastWall;
        private static double _lastProcessCpuMs;
        private static bool _hasSystemBaseline;
        private static long _lastIdle;
        private static long _lastKernel;
        private static long _lastUser;
        private static bool _systemTimesUnavailable;
        private static bool _disabled;

        public static ClientMachineSample Sample(double nowSeconds)
        {
            if (_disabled) return default;
            try
            {
                if (_process == null) _process = Process.GetCurrentProcess();
                _process.Refresh();
                double cpuMs = _process.TotalProcessorTime.TotalMilliseconds;
                double perSecond = _lastWall > 0d
                    ? CpuUsageMath.ProcessMsPerSecond(cpuMs - _lastProcessCpuMs, nowSeconds - _lastWall)
                    : -1d;
                _lastWall = nowSeconds;
                _lastProcessCpuMs = cpuMs;
                int memoryMiB = (int)Math.Min(int.MaxValue, _process.WorkingSet64 / (1024L * 1024L));
                return new ClientMachineSample(perSecond, SampleSystemCpu(), memoryMiB);
            }
            catch (Exception)
            {
                _disabled = true;
                return default;
            }
        }

        private static double SampleSystemCpu()
        {
            if (_systemTimesUnavailable) return -1d;
            try
            {
                if (!GetSystemTimes(out long idle, out long kernel, out long user)) return -1d;
                double percent = -1d;
                if (_hasSystemBaseline)
                {
                    percent = CpuUsageMath.SystemPercent(
                        (ulong)Math.Max(0L, idle - _lastIdle),
                        (ulong)Math.Max(0L, kernel - _lastKernel),
                        (ulong)Math.Max(0L, user - _lastUser));
                }
                _hasSystemBaseline = true;
                _lastIdle = idle;
                _lastKernel = kernel;
                _lastUser = user;
                return percent;
            }
            catch (Exception)
            {
                // Linux/Wine sem kernel32 ou sem permissao: so o campo do sistema fica "nao medido".
                _systemTimesUnavailable = true;
                return -1d;
            }
        }
    }
}

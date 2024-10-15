using Microsoft.Extensions.Logging;
using NLog;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CTNewGetPic
{
    public class GlobalMemMonitor
    {
        private const float MB_DECISION = 1 << 20;
        private const float GB_DECISION = 1 << 30;

        [ModuleInitializer]
        public static void ModuleInit()
        {
            LogManager.GetCurrentClassLogger().Info("---module init---");
            new Thread(async () =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
                var memInfo = new MEMORYSTATUSEX();
                while (await timer.WaitForNextTickAsync() && GlobalMemoryStatusEx(memInfo))
                {
                    try
                    {
                        using (Process proc = Process.GetCurrentProcess())
                        {
                            logger.Info("mem percentage: {0}, current mem: {1:F2} M, total mem: {2:F2} G", memInfo.dwMemoryLoad, proc.PrivateMemorySize64 / MB_DECISION, memInfo.ullTotalPhys / GB_DECISION);
                            if (proc.PrivateMemorySize64 > memInfo.ullTotalPhys * .5 || memInfo.dwMemoryLoad > 90)
                            {
                                CancellationTokenSource.Cancel();
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error("gmm error: {0}", ex);
                    }
                }
                logger.Info("monitor thread exiting");
                CancellationTokenSource.Cancel();
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest
            }.Start();
        }

        public static CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
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

            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }
    }
}
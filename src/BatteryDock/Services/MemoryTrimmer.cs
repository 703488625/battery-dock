using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace BatteryDock.Services;

internal static class MemoryTrimmer
{
    private const int MaxSweepPasses = 3;
    private static readonly TimeSpan SweepDelay = TimeSpan.FromSeconds(1.5);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr processHandle);

    public static async Task TrimAsync(
        Func<bool> canContinue,
        CancellationToken cancellationToken = default)
    {
        try
        {
            for (var pass = 0; pass < MaxSweepPasses; pass++)
            {
                if (!canContinue()) return;

                if (pass == 0)
                {
                    // WPF render targets and other large native buffers often sit
                    // on the LOH or are released by finalizers. Compact it once,
                    // then give finalizer chains a moment to enqueue new work.
                    GCSettings.LargeObjectHeapCompactionMode =
                        GCLargeObjectHeapCompactionMode.CompactOnce;
                }

                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);

                using (var process = Process.GetCurrentProcess())
                {
                    Logger.Info(
                        $"内存整理第 {pass + 1}/{MaxSweepPasses} 轮：" +
                        $"工作集 {process.WorkingSet64 / 1048576} MB，" +
                        $"提交 {process.PrivateMemorySize64 / 1048576} MB");
                }

                if (pass == MaxSweepPasses - 1) break;

                await Task.Delay(SweepDelay, cancellationToken);
            }

            if (!canContinue()) return;

            using var current = Process.GetCurrentProcess();
            if (EmptyWorkingSet(current.Handle))
            {
                Logger.Info(
                    $"内存整理完成：工作集 {current.WorkingSet64 / 1048576} MB，" +
                    $"提交 {current.PrivateMemorySize64 / 1048576} MB");
            }
        }
        catch (OperationCanceledException)
        {
            // The panel opened or the application is exiting.
        }
        catch (Exception ex)
        {
            Logger.Error("内存整理", ex);
        }
    }
}

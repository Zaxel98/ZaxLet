using System;
using System.Diagnostics;
using System.Threading;

namespace ZaxLet;

/// <summary>
/// Process stall diagnostics: dedicated thread sleeps 100 ms and logs if it wakes up late (process/OS freeze)
/// or if work queued to the thread pool runs late (thread pool starvation). Only active while phone is connected.
/// </summary>
static class StallWatch
{
    static int _gen;

    public static void Start()
    {
        int g = Interlocked.Increment(ref _gen);
        new Thread(() => Loop(g)) { IsBackground = true, Name = "zaxlet-stallwatch" }.Start();
    }

    public static void Stop() => Interlocked.Increment(ref _gen);

    static void Loop(int g)
    {
        var ev = new ManualResetEventSlim(false);
        while (Volatile.Read(ref _gen) == g)
        {
            long t0 = Stopwatch.GetTimestamp();
            Thread.Sleep(100);
            long sleepMs = (Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency;
            ev.Reset();
            long q0 = Stopwatch.GetTimestamp();
            ThreadPool.QueueUserWorkItem(_ => ev.Set());
            bool ok = ev.Wait(5000);
            long poolMs = (Stopwatch.GetTimestamp() - q0) * 1000 / Stopwatch.Frequency;
            if (sleepMs > 350 || poolMs > 250)
                PhoneAudio.Diag($"JEDA proses: tidur100ms={sleepMs}ms antre-pool={poolMs}ms{(ok ? "" : " (timeout)")} threads={ThreadPool.ThreadCount} pend={ThreadPool.PendingWorkItemCount} gcPause={(long)GC.GetTotalPauseDuration().TotalMilliseconds}ms");
        }
    }
}

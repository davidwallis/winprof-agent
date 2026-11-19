using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

static class FxBusy
{
    static readonly Random Rng = new Random();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Spin(int iterations)
    {
        double s = 0;
        for (int i = 0; i < iterations; i++) s += Math.Sqrt(i) * Math.Sin(i);
        return s;
    }

    // Burns CPU for a given time (a Sleep would not show up in a CPU profile).
    [MethodImpl(MethodImplOptions.NoInlining)]
    static double BurnMs(int ms)
    {
        double s = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) s += Spin(1000);
        return s;
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static double Level3() { return Spin(50000); }
    [MethodImpl(MethodImplOptions.NoInlining)] static double Level2() { return Level3() + Spin(20000); }
    [MethodImpl(MethodImplOptions.NoInlining)] static double Level1() { return Level2() + Spin(10000); }

    // The mystery function: only runs ~1 in 4 loops and burns a random 50-400 ms.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static double RandomHotspot() { return BurnMs(Rng.Next(50, 400)); }

    static readonly object Gate = new object();

    // Holds the lock for 30 ms at a time so Contend() has to wait for it.
    static void StartLockHolder()
    {
        var th = new System.Threading.Thread(() =>
        {
            while (true)
            {
                lock (Gate) System.Threading.Thread.Sleep(30);
                System.Threading.Thread.Sleep(10);
            }
        });
        th.IsBackground = true;
        th.Start();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Contend()
    {
        lock (Gate) return 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Allocate()
    {
        var list = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 2000; i++) list.Add(new string('x', 200));
        return list.Count + new byte[256 * 1024].Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Iteration()
    {
        double t = Level1();
        if (Rng.Next(4) == 0) t += RandomHotspot();
        t += Allocate();
        t += Contend();
        return t;
    }

    static void Main()
    {
        StartLockHolder();
        double t = 0;
        while (true) t += Iteration();
    }
}

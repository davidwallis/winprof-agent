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

    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Iteration()
    {
        double t = Level1();
        if (Rng.Next(4) == 0) t += RandomHotspot();
        return t;
    }

    static void Main()
    {
        double t = 0;
        while (true) t += Iteration();
    }
}

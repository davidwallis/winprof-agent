using System.Diagnostics;
using System.Runtime.CompilerServices;

var end = DateTime.UtcNow.AddSeconds(300);
double total = 0;
while (DateTime.UtcNow < end) total += Busy.Iteration();
Console.WriteLine(total);

static class Busy
{
    static readonly Random Rng = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Spin(int iterations)
    {
        double s = 0;
        for (var i = 0; i < iterations; i++) s += Math.Sqrt(i) * Math.Sin(i);
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

    [MethodImpl(MethodImplOptions.NoInlining)] static double Level3() => Spin(50000);
    [MethodImpl(MethodImplOptions.NoInlining)] static double Level2() => Level3() + Spin(20000);
    [MethodImpl(MethodImplOptions.NoInlining)] static double Level1() => Level2() + Spin(10000);

    // The mystery function: only runs ~1 in 4 loops and burns a random 50-400 ms.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static double RandomHotspot() => BurnMs(Rng.Next(50, 400));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Iteration()
    {
        var t = Level1();
        if (Rng.Next(4) == 0) t += RandomHotspot();
        return t;
    }
}

using System.Diagnostics;
using System.Runtime.CompilerServices;

var end = DateTime.UtcNow.AddSeconds(300);
double total = 0;
Busy.StartLockHolder();
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
        t += Allocate();
        t += Contend();
        return t;
    }

    static readonly object Gate = new();

    // Holds the lock for 30 ms at a time so Contend() has to wait for it.
    public static void StartLockHolder() =>
        new Thread(() =>
        {
            while (true)
            {
                lock (Gate) Thread.Sleep(30);
                Thread.Sleep(10);
            }
        }) { IsBackground = true }.Start();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Contend()
    {
        lock (Gate) return 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Allocate()
    {
        var list = new List<string>();
        for (var i = 0; i < 2000; i++) list.Add(new string('x', 200));
        return list.Count + new byte[256 * 1024].Length;
    }
}

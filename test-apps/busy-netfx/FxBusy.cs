using System;

static class FxBusy
{
    static double Work(double x)
    {
        double s = 0;
        for (int i = 0; i < 200000; i++) s += Math.Sqrt(x + i) * Math.Sin(i);
        return s;
    }

    static double Outer(double x) { return Work(x) + Work(x + 1); }

    static void Main()
    {
        double t = 0;
        while (true) t += Outer(t % 100);
    }
}

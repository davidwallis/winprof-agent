using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

var end = DateTime.UtcNow.AddSeconds(300);
double total = 0;
Busy.StartLockHolder();
Busy.StartIoThreads();
Busy.StartPoolWork();
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

    // Writes and reads a temp file, and echoes bytes over loopback TCP, so disk and network profiles have data.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BlockingWork() => Thread.Sleep(50);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void OpenCloseHandle() { using var e = new ManualResetEvent(false); }

    // Floods the thread pool with blocking items so they queue, and churns kernel handles.
    public static void StartPoolWork()
    {
        new Thread(() =>
        {
            while (true)
            {
                for (var i = 0; i < 200; i++) ThreadPool.QueueUserWorkItem(_ => BlockingWork());
                for (var i = 0; i < 100; i++) OpenCloseHandle();
                Thread.Sleep(500);
            }
        }) { IsBackground = true }.Start();
    }

    public static void StartIoThreads()
    {
        var path = Path.Combine(Path.GetTempPath(), "winprof-busy-io.bin");
        new Thread(() =>
        {
            var buf = new byte[256 * 1024];
            while (true)
            {
                WriteFile(path, buf);
                ReadFile(path, buf);
                Thread.Sleep(50);
            }
        }) { IsBackground = true }.Start();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        new Thread(() =>
        {
            using var c = listener.AcceptTcpClient();
            var s = c.GetStream();
            var b = new byte[16 * 1024];
            int n;
            while ((n = s.Read(b, 0, b.Length)) > 0) s.Write(b, 0, n);
        }) { IsBackground = true }.Start();
        new Thread(() =>
        {
            using var c = new TcpClient();
            c.Connect((IPEndPoint)listener.LocalEndpoint);
            var s = c.GetStream();
            var b = new byte[16 * 1024];
            while (true)
            {
                SendEcho(s, b);
                Thread.Sleep(20);
            }
        }) { IsBackground = true }.Start();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void WriteFile(string path, byte[] buf) => File.WriteAllBytes(path, buf);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ReadFile(string path, byte[] buf) => File.ReadAllBytes(path).Length;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SendEcho(NetworkStream s, byte[] b)
    {
        s.Write(b, 0, b.Length);
        var read = 0;
        while (read < b.Length) read += s.Read(b, read, b.Length - read);
    }

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

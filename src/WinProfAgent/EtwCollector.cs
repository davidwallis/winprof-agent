using System.Diagnostics;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Extensions.Options;
using WinProfAgent.Pprof;

namespace WinProfAgent;

public sealed class EtwCollector(
    IOptions<AgentOptions> options,
    ProfileQueue queue,
    AgentMetrics metrics,
    ILogger<EtwCollector> logger) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), TaskCreationOptions.LongRunning);

    private void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RunEpoch(ct);
                if (!ct.IsCancellationRequested) metrics.SessionRestarts.Add(1);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ETW session failed; retrying in 10s");
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            }
        }
    }

    /// <summary>One ETW session lifetime: runs N windows, then returns so the TraceLog memory can be released.</summary>
    private void RunEpoch(CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(Math.Max(1, _opt.WindowSeconds));
        var maxWindows = Math.Max(1, _opt.SessionRecycleWindows);
        var periodNanos = 1_000_000_000L / Math.Max(1, _opt.SampleHz);
        var host = Environment.MachineName;

        using var session = new TraceEventSession("WinProfAgent")
        {
            BufferSizeMB = _opt.BufferSizeMB,
            CpuSampleIntervalMSec = (float)(1000.0 / Math.Max(1, _opt.SampleHz)),
        };
        using var reg = ct.Register(() => TryStop(session));

        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Profile |
            KernelTraceEventParser.Keywords.ImageLoad |
            KernelTraceEventParser.Keywords.Process,
            KernelTraceEventParser.Keywords.Profile);

        // Runtime events give managed (JIT) method names; rundown covers processes that started before us.
        session.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Informational,
            (ulong)(ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.NGen | ClrTraceEventParser.Keywords.Loader));
        session.EnableProvider(ClrRundownTraceEventParser.ProviderGuid, TraceEventLevel.Verbose,
            (ulong)(ClrRundownTraceEventParser.Keywords.Jit | ClrRundownTraceEventParser.Keywords.NGen |
                    ClrRundownTraceEventParser.Keywords.Loader | ClrRundownTraceEventParser.Keywords.StartEnumeration));

        using var source = TraceLog.CreateFromTraceEventSession(session);
        var log = source.TraceLog;

        var symbolPath = BuildSymbolPath(_opt.Symbols);
        using var symbolReader = new SymbolReader(TextWriter.Null, symbolPath)
        {
            SecurityCheck = _ => true,
            Options = _opt.Symbols.AllowDownload ? SymbolReaderOptions.None : SymbolReaderOptions.CacheOnly,
        };

        var counts = new Dictionary<(int Pid, CallStackIndex Stack), int>();
        var processNames = new Dictionary<int, string>();
        var nameCache = new Dictionary<CodeAddressIndex, string>();
        var symbolizedModules = new HashSet<ModuleFileIndex>();
        var windowStart = DateTimeOffset.UtcNow;
        var windowsDone = 0;
        long lastLost = 0;

        source.Kernel.PerfInfoSample += data =>
        {
            var pid = data.ProcessID;
            if (pid <= 0) return;

            if (!processNames.TryGetValue(pid, out var name))
            {
                name = NormalizeName(data.ProcessName, pid);
                processNames[pid] = name;
            }
            if (!Allowed(name)) return;

            var stack = data.CallStack();
            if (stack is null) return;

            ref var c = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counts, (pid, stack.CallStackIndex), out _);
            c++;

            var now = DateTimeOffset.UtcNow;
            if (now - windowStart < window) return;

            Flush(counts, processNames, nameCache, symbolizedModules, log, symbolReader, windowStart, now, periodNanos, host);
            counts.Clear();
            windowStart = now;
            windowsDone++;

            var lost = session.EventsLost;
            if (lost > lastLost) metrics.EventsLost.Add(lost - lastLost);
            lastLost = lost;

            if (windowsDone >= maxWindows) Task.Run(() => TryStop(session));
        };

        logger.LogInformation("ETW session started: {Hz} Hz, {Window}s windows", _opt.SampleHz, window.TotalSeconds);
        source.Process();

        if (counts.Count > 0)
        {
            Flush(counts, processNames, nameCache, symbolizedModules, log, symbolReader, windowStart, DateTimeOffset.UtcNow, periodNanos, host);
        }
    }

    private void Flush(
        Dictionary<(int Pid, CallStackIndex Stack), int> counts,
        Dictionary<int, string> processNames,
        Dictionary<CodeAddressIndex, string> nameCache,
        HashSet<ModuleFileIndex> symbolizedModules,
        TraceLog log,
        SymbolReader reader,
        DateTimeOffset from,
        DateTimeOffset until,
        long periodNanos,
        string host)
    {
        var sw = Stopwatch.StartNew();
        long totalSamples = 0;
        var builders = new Dictionary<int, PprofBuilder>();
        var frames = new List<string>();

        foreach (var ((pid, stackIndex), count) in counts)
        {
            frames.Clear();
            for (var cs = log.CallStacks[stackIndex]; cs != null; cs = cs.Caller)
            {
                frames.Add(Resolve(cs.CodeAddress, nameCache, symbolizedModules, log, reader));
            }

            if (!builders.TryGetValue(pid, out var b)) builders[pid] = b = new PprofBuilder();
            b.AddSample(frames, count * periodNanos);
            totalSamples += count;
        }

        foreach (var (pid, b) in builders)
        {
            var name = processNames[pid];
            var labels = new Dictionary<string, string>(_opt.Pyroscope.Labels)
            {
                ["host"] = host,
                ["pid"] = pid.ToString(),
                ["process"] = name,
            };

            var bytes = b.Build(from, until - from, periodNanos);
            metrics.ProfileBytes.Record(bytes.Length);
            queue.TryWrite(new ProfilePayload(name, labels, from, until, bytes));
        }

        metrics.Samples.Add(totalSamples);
        metrics.Windows.Add(1);
        metrics.WindowProcessingDuration.Record(sw.Elapsed.TotalSeconds);
        logger.LogInformation("Window flushed: {Processes} processes, {Samples} samples, {Ms} ms",
            builders.Count, totalSamples, sw.ElapsedMilliseconds);
    }

    private static string Resolve(
        TraceCodeAddress ca,
        Dictionary<CodeAddressIndex, string> cache,
        HashSet<ModuleFileIndex> symbolized,
        TraceLog log,
        SymbolReader reader)
    {
        if (cache.TryGetValue(ca.CodeAddressIndex, out var cached)) return cached;

        var module = ca.ModuleFile;
        if (ca.Method is null && module is not null && symbolized.Add(module.ModuleFileIndex))
        {
            try { log.CodeAddresses.LookupSymbolsForModule(reader, module); }
            catch { /* unresolved modules fall back to module+offset */ }
        }

        var name = ca.FullMethodName;
        if (string.IsNullOrEmpty(name))
        {
            name = module is null ? $"0x{ca.Address:x}" : $"{module.Name}+0x{ca.Address - module.ImageBase:x}";
        }

        cache[ca.CodeAddressIndex] = name;
        return name;
    }

    private static string BuildSymbolPath(SymbolOptions o)
    {
        if (o.AllowDownload) return string.Join(';', o.PdbSearchPaths);

        // Offline: reduce SRV*cache*url entries to the local cache folder.
        var paths = new List<string>();
        foreach (var p in o.PdbSearchPaths)
        {
            if (!p.StartsWith("SRV*", StringComparison.OrdinalIgnoreCase)) { paths.Add(p); continue; }
            paths.AddRange(p.Split('*', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Where(s => !s.StartsWith("http", StringComparison.OrdinalIgnoreCase)));
        }
        return string.Join(';', paths);
    }

    private bool Allowed(string name)
    {
        var f = _opt.Processes;
        if (f.Exclude.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase))) return false;
        return f.Include.Count == 0 || f.Include.Any(i => string.Equals(i, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string? name, int pid)
    {
        if (string.IsNullOrEmpty(name))
        {
            try { name = Process.GetProcessById(pid).ProcessName; }
            catch { name = $"pid-{pid}"; }
        }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    private static void TryStop(TraceEventSession session)
    {
        try { session.Stop(); } catch (ObjectDisposedException) { }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Options;

namespace WinProfAgent;

public sealed class EtwCollector(
    IOptions<AgentOptions> options,
    SnapshotQueue snapshots,
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

        var counts = new Dictionary<(int Pid, CallStackIndex Stack), int>();
        var processNames = new Dictionary<int, string?>();
        var windowStart = DateTimeOffset.UtcNow;
        var windowsDone = 0;
        long lastLost = 0;

        source.Kernel.PerfInfoSample += data =>
        {
            var pid = data.ProcessID;
            if (pid <= 0) return;

            if (!processNames.TryGetValue(pid, out var name))
            {
                name = ResolveService(NormalizeName(data.ProcessName, pid));
                processNames[pid] = name;
            }
            if (name is null) return;

            var stack = data.CallStack();
            if (stack is null) return;

            ref var c = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, (pid, stack.CallStackIndex), out _);
            c++;

            var now = DateTimeOffset.UtcNow;
            if (now - windowStart < window) return;

            Snapshot(counts, processNames, log, windowStart, now, periodNanos);
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

        if (counts.Count > 0) Snapshot(counts, processNames, log, windowStart, DateTimeOffset.UtcNow, periodNanos);
    }

    /// <summary>Runs on the event thread: copies what the symbol worker needs and returns without touching PDBs.</summary>
    private void Snapshot(
        Dictionary<(int Pid, CallStackIndex Stack), int> counts,
        Dictionary<int, string?> processNames,
        TraceLog log,
        DateTimeOffset from,
        DateTimeOffset until,
        long periodNanos)
    {
        var frames = new List<FrameInfo>();
        var frameIds = new Dictionary<CodeAddressIndex, int>();
        var processes = new Dictionary<int, ProcessSnapshot>();
        var ids = new List<int>();

        foreach (var ((pid, stackIndex), count) in counts)
        {
            ids.Clear();
            for (var cs = log.CallStacks[stackIndex]; cs != null; cs = cs.Caller)
            {
                var ca = cs.CodeAddress;
                if (!frameIds.TryGetValue(ca.CodeAddressIndex, out var id))
                {
                    id = frames.Count;
                    frames.Add(DescribeFrame(ca));
                    frameIds[ca.CodeAddressIndex] = id;
                }
                ids.Add(id);
            }
            if (ids.Count == 0) continue;

            if (!processes.TryGetValue(pid, out var p))
            {
                processes[pid] = p = new ProcessSnapshot { Pid = pid, Name = processNames[pid]! };
            }
            p.Stacks.Add(new StackSample { Frames = ids.ToArray(), Count = count });
        }

        // Downloads are only ever allowed for processes         explicitly listed in Targets.
                var allowDownload = _opt.Symbols.AllowDownload && _opt.Processes.Targets.Count > 0;

        snapshots.TryWrite(new WindowSnapshot
        {
            From = from,
            Until = until,
            PeriodNanos = periodNanos,
            Host = Environment.MachineName,
            AllowDownload = allowDownload,
            Frames = frames.ToArray(),
            Processes = processes.Values.ToList(),
        });
    }

    private static FrameInfo DescribeFrame(TraceCodeAddress ca)
    {
        var module = ca.ModuleFile;

        // Managed (JIT) frames already have a method name from the runtime events.
        var managed = ca.FullMethodName;
        if (!string.IsNullOrEmpty(managed)) return new FrameInfo { Name = managed };

        if (module is null) return new FrameInfo { Name = $"0x{ca.Address:x}" };

        return new FrameInfo
        {
            ModuleName = module.Name,
            ModulePath = module.FilePath,
            PdbName = module.PdbName,
            PdbGuid = module.PdbSignature,
            PdbAge = module.PdbAge,
            Rva = (uint)(ca.Address - module.ImageBase),
        };
    }

    /// <summary>Returns the Pyroscope service name for a process, or null when it should not be profiled.</summary>
    private string? ResolveService(string processName)
    {
        var f = _opt.Processes;
        if (f.Exclude.Any(e => SameProcess(e, processName))) return null;
        if (f.Targets.Count == 0) return processName;

        var target = f.Targets.FirstOrDefault(t => SameProcess(t.Process, processName));
        if (target is null) return null;
        return string.IsNullOrWhiteSpace(target.Name) ? processName : target.Name;
    }

    private static bool SameProcess(string configured, string processName)
    {
        if (configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) configured = configured[..^4];
        return string.Equals(configured, processName, StringComparison.OrdinalIgnoreCase);
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

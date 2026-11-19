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
    private static readonly Guid KernelNetworkProvider = new("7dd42a49-5329-4832-8dfd-43d979153a88");

    // Microsoft-Windows-Kernel-Network event ids for data sent/received (IPv4 and IPv6).
    private static readonly Dictionary<int, string> NetworkEvents = new()
    {
        [10] = "tcp send", [11] = "tcp recv", [26] = "tcp send", [27] = "tcp recv",
        [42] = "udp send", [43] = "udp recv", [58] = "udp send", [59] = "udp recv",
    };

    private const int AllocationTickEventId = 10;
    private const int ContentionStartEventId = 81;

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

        var kernelKeywords = KernelTraceEventParser.Keywords.Profile |
            KernelTraceEventParser.Keywords.ImageLoad |
            KernelTraceEventParser.Keywords.Process;
        var kernelStacks = KernelTraceEventParser.Keywords.Profile;
        if (_opt.CollectDisk)
        {
            kernelKeywords |= KernelTraceEventParser.Keywords.FileIOInit;
            kernelStacks |= KernelTraceEventParser.Keywords.FileIOInit;
        }
        if (_opt.CollectWaits)
        {
            kernelKeywords |= KernelTraceEventParser.Keywords.ContextSwitch;
            kernelStacks |= KernelTraceEventParser.Keywords.ContextSwitch;
        }
        session.EnableKernelProvider(kernelKeywords, kernelStacks);

        if (_opt.CollectNetwork)
        {
            // The classic kernel TCP/IP events carry no stacks, so use the manifest-based provider instead.
            var netOptions = new TraceEventProviderOptions { StacksEnabled = true, EventIDStacksToEnable = NetworkEvents.Keys.ToList() };
            session.EnableProvider(KernelNetworkProvider, TraceEventLevel.Informational, 0x30, netOptions);
        }

        // Runtime events give managed (JIT) method names; rundown covers processes that started before us.
        var clrKeywords = ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.NGen | ClrTraceEventParser.Keywords.Loader;
        var clrOptions = new TraceEventProviderOptions { StacksEnabled = true };
        clrOptions.EventIDStacksToEnable = new List<int>();
        if (_opt.CollectAllocations)
        {
            clrKeywords |= ClrTraceEventParser.Keywords.GC;
            clrOptions.EventIDStacksToEnable.Add(AllocationTickEventId);
        }
        if (_opt.CollectContentions)
        {
            clrKeywords |= ClrTraceEventParser.Keywords.Contention;
            clrOptions.EventIDStacksToEnable.Add(ContentionStartEventId);
        }
        if (clrOptions.EventIDStacksToEnable.Count == 0) clrOptions.StacksEnabled = false;
        session.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, (ulong)clrKeywords, clrOptions);
        session.EnableProvider(ClrRundownTraceEventParser.ProviderGuid, TraceEventLevel.Verbose,
            (ulong)(ClrRundownTraceEventParser.Keywords.Jit | ClrRundownTraceEventParser.Keywords.NGen |
                    ClrRundownTraceEventParser.Keywords.Loader | ClrRundownTraceEventParser.Keywords.StartEnumeration));

        using var source = TraceLog.CreateFromTraceEventSession(session);
        var log = source.TraceLog;

        // TraceLog resolves JIT methods lazily and misses processes that started before us, so keep our own table
        // fed by both the live JIT events and the rundown events emitted when the session starts.
        var methods = new ManagedMethodMap();
        void OnMethod(MethodLoadUnloadVerboseTraceData d) =>
            methods.Add(d.ProcessID, d.MethodStartAddress, d.MethodSize, d.MethodNamespace, d.MethodName, d.MethodSignature);
        source.Clr.MethodLoadVerbose += OnMethod;
        source.Clr.MethodDCStartVerboseV2 += OnMethod;
        source.Clr.MethodDCStopVerboseV2 += OnMethod;
        var rundown = new ClrRundownTraceEventParser(source);
        rundown.MethodDCStartVerbose += OnMethod;
        rundown.MethodDCStopVerbose += OnMethod;

        // Explicitly ask already-running .NET Framework processes to emit their method rundown.
        session.CaptureState(ClrTraceEventParser.ProviderGuid,
            (ulong)(ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.NGen | ClrTraceEventParser.Keywords.Loader));
        // CaptureState re-enables the provider without our options, so apply them again (this restores stack capture).
        session.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, (ulong)clrKeywords, clrOptions);

        var data = new WindowData();
        var counts = data.Cpu;
        var processNames = new Dictionary<int, string?>();
        var windowStart = DateTimeOffset.UtcNow;
        var windowsDone = 0;
        long lastLost = 0;

        // The kernel's sampling interval is system-wide and may differ from what we asked for, so measure it
        // from the median gap between consecutive non-idle samples on the same CPU (idle CPUs sample sparsely).
        var lastSample = new Dictionary<int, (double Ms, int Pid)>();
        var gaps = new List<double>();

        var commandLines = new Dictionary<int, string>();
        var imageNames = new Dictionary<int, string>();
        source.Kernel.ProcessStartGroup += p =>
        {
            if (!string.IsNullOrEmpty(p.ProcessName)) imageNames[p.ProcessID] = p.ProcessName;
            if (!string.IsNullOrEmpty(p.CommandLine)) commandLines[p.ProcessID] = p.CommandLine;
            processNames.Remove(p.ProcessID);
        };
        source.Kernel.ProcessStop += p =>
        {
            commandLines.Remove(p.ProcessID);
            imageNames.Remove(p.ProcessID);
            processNames.Remove(p.ProcessID);
        };

        string? ServiceFor(int pid, string? processName)
        {
            if (processNames.TryGetValue(pid, out var name)) return name;
            var image = NormalizeName(processName, pid);
            commandLines.TryGetValue(pid, out var commandLine);
            name = ResolveService(image, commandLine);
            processNames[pid] = name;
            if (name is not null && _opt.Processes.Targets.Count > 0)
            {
                logger.LogInformation("Profiling {Process} (pid {Pid}) as service {Service}", image, pid, name);
            }
            return name;
        }

        var allocPending = new List<(int Pid, GCAllocationTickTraceData Event)>();
        var contentionPending = new List<(ContentionStartTraceData Start, long Nanos)>();
        const double StackDelayMs = 1000;
        void DrainPending(double nowMs)
        {
            var keep = 0;
            for (var i = 0; i < allocPending.Count; i++)
            {
                var (apid, a) = allocPending[i];
                if (nowMs - a.TimeStampRelativeMSec < StackDelayMs) { allocPending[keep++] = allocPending[i]; continue; }
                var stack = a.CallStack();
                if (stack is null) { data.AllocNoStack++; continue; }
                var type = string.IsNullOrEmpty(a.TypeName) ? "<unknown>" : a.TypeName;
                ref var v = ref CollectionsMarshal.GetValueRefOrAddDefault(data.Allocs, (apid, stack.CallStackIndex, type), out _);
                v = (v.Objects + 1, v.Bytes + a.AllocationAmount64);
            }
            allocPending.RemoveRange(keep, allocPending.Count - keep);

            keep = 0;
            for (var i = 0; i < contentionPending.Count; i++)
            {
                var (start, nanos) = contentionPending[i];
                if (nowMs - start.TimeStampRelativeMSec < StackDelayMs) { contentionPending[keep++] = contentionPending[i]; continue; }
                var stack = start.CallStack();
                if (stack is null) { data.ContentionNoStack++; continue; }
                ref var v = ref CollectionsMarshal.GetValueRefOrAddDefault(data.Contentions, (start.ProcessID, stack.CallStackIndex), out _);
                v = (v.Count + 1, v.Nanos + nanos);
            }
            contentionPending.RemoveRange(keep, contentionPending.Count - keep);
        }

        if (_opt.CollectAllocations)
        {
            source.Clr.GCAllocationTick += a =>
            {
                if (a.ProcessID <= 0 || ServiceFor(a.ProcessID, a.ProcessName) is null) return;
                data.AllocEvents++;
                // The stack arrives as a separate event right after this one, so resolve it a little later.
                allocPending.Add((a.ProcessID, (GCAllocationTickTraceData)a.Clone()));
            };
        }

        if (_opt.CollectContentions)
        {
            var pending = new Dictionary<(int Pid, int Tid), ContentionStartTraceData>();
            source.Clr.ContentionStart += c =>
            {
                if (c.ProcessID <= 0 || ServiceFor(c.ProcessID, c.ProcessName) is null) return;
                data.ContentionEvents++;
                pending[(c.ProcessID, c.ThreadID)] = (ContentionStartTraceData)c.Clone();
            };
            source.Clr.ContentionStop += c =>
            {
                if (!pending.Remove((c.ProcessID, c.ThreadID), out var start)) return;
                var nanos = (long)((c.TimeStampRelativeMSec - start.TimeStampRelativeMSec) * 1_000_000);
                contentionPending.Add((start, Math.Max(0, nanos)));
            };
        }

        void AddKernel(Dictionary<(int Pid, CallStackIndex Stack, string Kind), (long Count, long Value)> map, TraceEvent e, int pid, string kind, long value)
        {
            if (pid <= 0 || ServiceFor(pid, e.ProcessName) is null) return;
            var stack = e.CallStack();
            data.CountKernel(kind, stack is not null);
            if (stack is null) return;
            ref var v = ref CollectionsMarshal.GetValueRefOrAddDefault(map, (pid, stack.CallStackIndex, kind), out _);
            v = (v.Count + 1, v.Value + value);
        }

        if (_opt.CollectDisk)
        {
            source.Kernel.FileIORead += e => AddKernel(data.Disk, e, e.ProcessID, "read", e.IoSize);
            source.Kernel.FileIOWrite += e => AddKernel(data.Disk, e, e.ProcessID, "write", e.IoSize);
        }
        if (_opt.CollectNetwork)
        {
            source.Dynamic.All += e =>
            {
                if (e.ProviderGuid != KernelNetworkProvider || !NetworkEvents.TryGetValue((int)e.ID, out var kind)) return;
                var size = Convert.ToInt64(e.PayloadByName("size") ?? 0);
                AddKernel(data.Network, e, e.ProcessID, kind, size);
            };
        }
        if (_opt.CollectWaits)
        {
            // The stack on a context switch is the outgoing thread's wait stack; the wait ends when that thread is next switched in.
            var waiting = new Dictionary<int, (int Pid, CallStackIndex Stack, double Ms)>();
            source.Kernel.ThreadCSwitch += e =>
            {
                if (waiting.Remove(e.NewThreadID, out var w))
                {
                    ref var v = ref CollectionsMarshal.GetValueRefOrAddDefault(data.Waits, (w.Pid, w.Stack), out _);
                    v = (v.Count + 1, v.Value + (long)((e.TimeStampRelativeMSec - w.Ms) * 1_000_000));
                }
                var oldPid = e.ProcessID;
                if (oldPid <= 0 || e.ThreadID <= 0 || ServiceFor(oldPid, imageNames.GetValueOrDefault(oldPid)) is null) return;
                var stack = e.CallStack();
                data.CountKernel("wait", stack is not null);
                if (stack is null) return;
                waiting[e.ThreadID] = (oldPid, stack.CallStackIndex, e.TimeStampRelativeMSec);
            };
        }

        source.Kernel.PerfInfoSample += d =>
        {
            if (allocPending.Count + contentionPending.Count > 0) DrainPending(d.TimeStampRelativeMSec);
            var cpu = d.ProcessorNumber;
            var ts = d.TimeStampRelativeMSec;
            var samplePid = d.ProcessID;
            if (lastSample.TryGetValue(cpu, out var prev) && samplePid > 0 && prev.Pid > 0 && gaps.Count < 50_000)
            {
                var gap = ts - prev.Ms;
                if (gap > 0 && gap < 100) gaps.Add(gap);
            }
            lastSample[cpu] = (ts, samplePid);

            var pid = d.ProcessID;
            if (pid <= 0) return;

            var name = ServiceFor(pid, d.ProcessName);
            if (name is null) return;

            var stack = d.CallStack();
            if (stack is null) return;

            ref var c = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, (pid, stack.CallStackIndex), out _);
            c++;

            var now = DateTimeOffset.UtcNow;
            if (now - windowStart < window) return;

            Snapshot(data, processNames, log, windowStart, now, MeasurePeriod(gaps, periodNanos, logger), methods);
            data.Clear();
            gaps.Clear();
            windowStart = now;
            windowsDone++;

            var lost = session.EventsLost;
            if (lost > lastLost) metrics.EventsLost.Add(lost - lastLost);
            lastLost = lost;

            if (windowsDone >= maxWindows) Task.Run(() => TryStop(session));
        };

        logger.LogInformation("ETW session started: {Hz} Hz, {Window}s windows", _opt.SampleHz, window.TotalSeconds);
        LogTargets(logger);
        source.Process();

        if (!data.IsEmpty) Snapshot(data, processNames, log, windowStart, DateTimeOffset.UtcNow, MeasurePeriod(gaps, periodNanos, logger), methods);
    }

    private static long MeasurePeriod(List<double> gaps, long configuredNanos, ILogger logger)
    {
        if (gaps.Count < 100) return configuredNanos;

        gaps.Sort();
        var measured = (long)(gaps[gaps.Count / 2] * 1_000_000);
        if (Math.Abs(measured - configuredNanos) > configuredNanos / 5)
        {
            logger.LogWarning("Measured sampling interval {Measured:F2} ms differs from configured {Configured:F2} ms; using the measured value",
                measured / 1e6, configuredNanos / 1e6);
        }
        return measured;
    }

    private sealed class WindowData
    {
        public Dictionary<(int Pid, CallStackIndex Stack), int> Cpu { get; } = new();
        public Dictionary<(int Pid, CallStackIndex Stack, string Type), (long Objects, long Bytes)> Allocs { get; } = new();
        public Dictionary<(int Pid, CallStackIndex Stack), (long Count, long Nanos)> Contentions { get; } = new();
        public Dictionary<(int Pid, CallStackIndex Stack, string Kind), (long Count, long Value)> Disk { get; } = new();
        public Dictionary<(int Pid, CallStackIndex Stack, string Kind), (long Count, long Value)> Network { get; } = new();
        public Dictionary<(int Pid, CallStackIndex Stack), (long Count, long Value)> Waits { get; } = new();
        public long AllocEvents, AllocNoStack, ContentionEvents, ContentionNoStack;
        public Dictionary<string, (long Events, long NoStack)> Kernel { get; } = new();
        public void CountKernel(string kind, bool hasStack)
        {
            Kernel.TryGetValue(kind, out var k);
            Kernel[kind] = (k.Events + 1, k.NoStack + (hasStack ? 0 : 1));
        }
        public bool IsEmpty => Cpu.Count == 0 && Allocs.Count == 0 && Contentions.Count == 0 &&
            Disk.Count == 0 && Network.Count == 0 && Waits.Count == 0;

        public void Clear()
        {
            Cpu.Clear();
            Allocs.Clear();
            Contentions.Clear();
            Disk.Clear();
            Network.Clear();
            Waits.Clear();
            AllocEvents = AllocNoStack = ContentionEvents = ContentionNoStack = 0;
            Kernel.Clear();
        }
    }

    /// <summary>Runs on the event thread: copies what the symbol worker needs and returns without touching PDBs.</summary>
    private void Snapshot(
        WindowData data,
        Dictionary<int, string?> processNames,
        TraceLog log,
        DateTimeOffset from,
        DateTimeOffset until,
        long periodNanos,
        ManagedMethodMap methods)
    {
        logger.LogInformation("Window events: alloc ticks {A} ({AN} without stack), contentions {C} ({CN} without stack), kernel (events/without stack): {K}",
            data.AllocEvents, data.AllocNoStack, data.ContentionEvents, data.ContentionNoStack,
            string.Join(", ", data.Kernel.Select(k => $"{k.Key} {k.Value.Events}/{k.Value.NoStack}")));
        var frames = new List<FrameInfo>();
        var frameIds = new Dictionary<(int Pid, CodeAddressIndex Index), int>();
        var typeFrameIds = new Dictionary<string, int>();
        var processes = new Dictionary<int, ProcessSnapshot>();
        var ids = new List<int>();

        // Walks a call stack into ids (leaf first), optionally with a synthetic leaf such as an allocated type name.
        int[]? Walk(int pid, CallStackIndex stackIndex, string? leaf)
        {
            ids.Clear();
            if (leaf is not null)
            {
                if (!typeFrameIds.TryGetValue(leaf, out var leafId))
                {
                    typeFrameIds[leaf] = leafId = frames.Count;
                    frames.Add(new FrameInfo { Name = leaf });
                }
                ids.Add(leafId);
            }
            for (var cs = log.CallStacks[stackIndex]; cs != null; cs = cs.Caller)
            {
                var ca = cs.CodeAddress;
                if (!frameIds.TryGetValue((pid, ca.CodeAddressIndex), out var id))
                {
                    id = frames.Count;
                    frames.Add(DescribeFrame(ca, pid, methods));
                    frameIds[(pid, ca.CodeAddressIndex)] = id;
                }
                ids.Add(id);
            }
            return ids.Count == 0 ? null : ids.ToArray();
        }

        ProcessSnapshot Proc(int pid)
        {
            if (!processes.TryGetValue(pid, out var p))
            {
                processes[pid] = p = new ProcessSnapshot { Pid = pid, Name = processNames[pid]! };
            }
            return p;
        }

        foreach (var ((pid, stack), count) in data.Cpu)
        {
            if (Walk(pid, stack, null) is { } f) Proc(pid).Stacks.Add(new StackSample { Frames = f, Count = count });
        }
        foreach (var ((pid, stack, type), v) in data.Allocs)
        {
            if (Walk(pid, stack, type) is { } f) Proc(pid).AllocStacks.Add(new StackSample { Frames = f, Count = v.Objects, Value = v.Bytes });
        }
        foreach (var ((pid, stack), v) in data.Contentions)
        {
            if (Walk(pid, stack, null) is { } f) Proc(pid).ContentionStacks.Add(new StackSample { Frames = f, Count = v.Count, Value = v.Nanos });
        }
        foreach (var ((pid, stack, kind), v) in data.Disk)
        {
            if (Walk(pid, stack, kind) is { } f) Proc(pid).DiskStacks.Add(new StackSample { Frames = f, Count = v.Count, Value = v.Value });
        }
        foreach (var ((pid, stack, kind), v) in data.Network)
        {
            if (Walk(pid, stack, kind) is { } f) Proc(pid).NetworkStacks.Add(new StackSample { Frames = f, Count = v.Count, Value = v.Value });
        }
        foreach (var ((pid, stack), v) in data.Waits)
        {
            if (Walk(pid, stack, null) is { } f) Proc(pid).WaitStacks.Add(new StackSample { Frames = f, Count = v.Count, Value = v.Value });
        }
        // Downloads are only ever allowed for processes explicitly listed in Targets.
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

    private static FrameInfo DescribeFrame(TraceCodeAddress ca, int pid, ManagedMethodMap methods)
    {
        var module = ca.ModuleFile;

        // Managed (JIT) frames already have a method name from the runtime events.
        var managed = ca.FullMethodName;
        if (!string.IsNullOrEmpty(managed)) return new FrameInfo { Name = managed };

        // Otherwise (e.g. the process started before us) fall back to our own method table.
        var jitted = methods.Lookup(pid, ca.Address);
        if (jitted is not null) return new FrameInfo { Name = jitted };

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

    private void LogTargets(ILogger logger)
    {
        var p = _opt.Processes;
        if (p.Targets.Count == 0)
        {
            logger.LogWarning("No Processes:Targets configured: profiling every process (excluding {Exclude})", string.Join(", ", p.Exclude));
            return;
        }

        foreach (var t in p.Targets)
        {
            logger.LogInformation("Target: process {Process} {CommandLine} -> service {Service}", t.Process,
                string.IsNullOrEmpty(t.CommandLineContains) ? "" : $"(command line contains {t.CommandLineContains})",
                string.IsNullOrWhiteSpace(t.Name) ? t.Process : t.Name);
        }
    }

    /// <summary>Returns the Pyroscope service name for a process, or null when it should not be profiled.</summary>
    private string? ResolveService(string processName, string? commandLine)
    {
        var f = _opt.Processes;
        if (f.Exclude.Any(e => SameProcess(e, processName))) return null;
        if (f.Targets.Count == 0) return processName;

        var target = f.Targets.FirstOrDefault(t => SameProcess(t.Process, processName) && MatchesCommandLine(t, commandLine));
        if (target is null) return null;
        return string.IsNullOrWhiteSpace(target.Name) ? processName : target.Name;
    }

    private static bool MatchesCommandLine(TargetOptions t, string? commandLine) =>
        string.IsNullOrEmpty(t.CommandLineContains) ||
        (commandLine?.Contains(t.CommandLineContains, StringComparison.OrdinalIgnoreCase) ?? false);

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

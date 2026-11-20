using System.Diagnostics;
using System.Reflection.PortableExecutable;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Extensions.Options;
using WinProfAgent.Pprof;

namespace WinProfAgent;

/// <summary>Resolves native frames from PDBs and encodes windows, off the ETW event thread.</summary>
public sealed class SymbolWorker : BackgroundService
{
    private readonly record struct ModuleKey(string PdbName, Guid Guid, int Age);

    private sealed class ModuleSymbols
    {
        public NativeSymbolModule? Module;
        public DateTime FailedAtUtc;
        public bool TriedDownload;
        public readonly Dictionary<uint, string> Names = new();
    }

    private const int MaxNamesPerModule = 200_000;

    private readonly SnapshotQueue _snapshots;
    private readonly ProfileQueue _profiles;
    private readonly AgentMetrics _metrics;
    private readonly ILogger<SymbolWorker> _logger;
    private readonly AgentOptions _opt;
    private readonly SymbolReader _local;
    private readonly SymbolReader _remote;
    private readonly Dictionary<ModuleKey, ModuleSymbols> _modules = new();
    private readonly HashSet<ModuleKey> _reportedMissing = new();
    private readonly HashSet<string> _reportedNoDebugInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string? PdbName, Guid Guid, int Age)> _debugInfo = new(StringComparer.OrdinalIgnoreCase);

    public SymbolWorker(
        SnapshotQueue snapshots,
        ProfileQueue profiles,
        AgentMetrics metrics,
        IOptions<AgentOptions> options,
        ILogger<SymbolWorker> logger)
    {
        _snapshots = snapshots;
        _profiles = profiles;
        _metrics = metrics;
        _logger = logger;
        _opt = options.Value;

        var s = _opt.Symbols;
        if (!string.IsNullOrWhiteSpace(s.SymbolFolder)) Directory.CreateDirectory(s.SymbolFolder);

        var localParts = s.PdbSearchPaths.Where(p => !p.StartsWith("SRV*", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(s.SymbolFolder)) localParts.Add($"SRV*{s.SymbolFolder}");

        var remoteParts = new List<string>(s.PdbSearchPaths);
        if (!string.IsNullOrWhiteSpace(s.SymbolFolder))
        {
            remoteParts.AddRange(s.SymbolServers.Select(url => $"SRV*{s.SymbolFolder}*{url}"));
        }

        _local = new SymbolReader(TextWriter.Null, string.Join(';', localParts))
        {
            SecurityCheck = _ => true,
            Options = SymbolReaderOptions.CacheOnly,
        };
        _remote = new SymbolReader(TextWriter.Null, string.Join(';', remoteParts))
        {
            SecurityCheck = _ => true,
            Options = SymbolReaderOptions.None,
        };

        if (s.AllowDownload && _opt.Processes.Targets.Count == 0)
        {
            _logger.LogWarning("Symbol downloads are disabled because Agent:Processes:Targets is empty");
        }

        // Profiling every process would warn for hundreds of modules; only warn for explicitly targeted processes.
        _missingLevel = _opt.Processes.Targets.Count > 0 ? LogLevel.Warning : LogLevel.Debug;
    }

    private readonly LogLevel _missingLevel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), TaskCreationOptions.LongRunning).Unwrap();

    private async Task Run(CancellationToken ct)
    {
        try
        {
            await foreach (var snap in _snapshots.ReadAllAsync(ct))
            {
                try { Process(snap); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to process window"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Process(WindowSnapshot snap)
    {
        var sw = Stopwatch.StartNew();

        foreach (var f in snap.Frames)
        {
            if (f.Name is null) f.Name = ResolveNative(f, snap.AllowDownload);
        }

        long totalSamples = 0;
        var frames = new List<string>();

        void Emit(ProcessSnapshot p, List<StackSample> stacks, PprofBuilder b, Func<StackSample, long[]> values, long period)
        {
            foreach (var st in stacks)
            {
                frames.Clear();
                foreach (var id in st.Frames) frames.Add(snap.Frames[id].Name!);
                b.AddSample(frames, values(st));
            }
            if (b.SampleCount == 0) return;

            var labels = new Dictionary<string, string>(_opt.Pyroscope.Labels)
            {
                ["host"] = snap.Host,
                ["pid"] = p.Pid.ToString(),
                ["process"] = p.Name,
            };

            var bytes = b.Build(snap.From, snap.Until - snap.From, period);
            _metrics.ProfileBytes.Record(bytes.Length);
            _profiles.TryWrite(new ProfilePayload(p.Name, labels, snap.From, snap.Until, bytes));
        }

        SampleType[] allocTypes = [new("alloc_objects", "count"), new("alloc_space", "bytes")];
        SampleType[] ioTypes = [new("disk_operations", "count"), new("disk_bytes", "bytes")];
        SampleType[] netTypes = [new("network_operations", "count"), new("network_bytes", "bytes")];
        SampleType[] waitTypes = [new("waits", "count"), new("wait_time", "nanoseconds")];
        SampleType[] poolTypes = [new("threadpool_adjustments", "count")];
        SampleType[] contentionTypes = [new("contentions", "count"), new("delay", "nanoseconds")];

        foreach (var p in snap.Processes)
        {
            Emit(p, p.Stacks, new PprofBuilder(), st => [st.Count * snap.PeriodNanos], snap.PeriodNanos);
            totalSamples += p.Stacks.Sum(s => s.Count);

            Emit(p, p.AllocStacks, new PprofBuilder(allocTypes, new("space", "bytes")), st => [st.Count, st.Value], 1);
            Emit(p, p.DiskStacks, new PprofBuilder(ioTypes, new("bytes", "bytes")), st => [st.Count, st.Value], 1);
            Emit(p, p.NetworkStacks, new PprofBuilder(netTypes, new("bytes", "bytes")), st => [st.Count, st.Value], 1);
            Emit(p, p.WaitStacks, new PprofBuilder(waitTypes, new("wait_time", "nanoseconds")), st => [st.Count, st.Value], 1);
            Emit(p, p.ThreadPoolStacks, new PprofBuilder(poolTypes, new("threadpool_adjustments", "count")), st => [st.Count], 1);
            Emit(p, p.ContentionStacks, new PprofBuilder(contentionTypes, new("contentions", "count")), st => [st.Count, st.Value], 1);
        }

        _metrics.Samples.Add(totalSamples);
        _metrics.Windows.Add(1);
        _metrics.WindowProcessingDuration.Record(sw.Elapsed.TotalSeconds);
        _logger.LogInformation("Window processed: {Processes} processes, {Samples} samples, {Ms} ms",
            snap.Processes.Count, totalSamples, sw.ElapsedMilliseconds);
    }

    private string ResolveNative(FrameInfo f, bool allowDownload)
    {
        var fallback = $"{f.ModuleName}+0x{f.Rva:x}";

        var (pdbName, guid, age) = (f.PdbName, f.PdbGuid, f.PdbAge);
        if ((string.IsNullOrEmpty(pdbName) || guid == Guid.Empty) && !string.IsNullOrEmpty(f.ModulePath))
        {
            (pdbName, guid, age) = GetDebugInfo(f.ModulePath, f.ModuleName);
        }
        if (string.IsNullOrEmpty(pdbName) || guid == Guid.Empty) return fallback;

        var key = new ModuleKey(pdbName, guid, age);
        var entry = GetModule(key, f, pdbName, guid, age, allowDownload);
        if (entry.Module is null) return fallback;

        if (entry.Names.TryGetValue(f.Rva, out var cached)) return cached;

        string name;
        try { name = entry.Module.FindNameForRva(f.Rva); }
        catch { name = ""; }
        if (string.IsNullOrEmpty(name)) name = fallback;

        if (entry.Names.Count >= MaxNamesPerModule) entry.Names.Clear();
        entry.Names[f.Rva] = name;
        return name;
    }

    private (string? PdbName, Guid Guid, int Age) GetDebugInfo(string modulePath, string moduleName)
    {
        if (_debugInfo.TryGetValue(modulePath, out var cached)) return cached;

        (string?, Guid, int) result = (null, Guid.Empty, 0);
        try
        {
            var path = ToDosPath(modulePath);
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                foreach (var entry in pe.ReadDebugDirectory())
                {
                    if (entry.Type != DebugDirectoryEntryType.CodeView) continue;
                    var cv = pe.ReadCodeViewDebugDirectoryData(entry);
                    result = (Path.GetFileName(cv.Path), cv.Guid, cv.Age);
                    break;
                }
            }
        }
        catch { /* not a readable PE */ }

        if (result.Item1 is null && _reportedNoDebugInfo.Add(modulePath))
        {
            _logger.Log(_missingLevel, "No debug info (or module file not found) for {Module} at {Path}", moduleName, modulePath);
        }

        _debugInfo[modulePath] = result;
        return result;
    }

    private static string ToDosPath(string p)
    {
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), p[@"\SystemRoot\".Length..]);
        }
        return p.StartsWith(@"\??\", StringComparison.Ordinal) ? p[4..] : p;
    }

    private ModuleSymbols GetModule(ModuleKey key, FrameInfo f, string pdbName, Guid guid, int age, bool allowDownload)
    {
        var retry = TimeSpan.FromMinutes(Math.Max(1, _opt.Symbols.MissingRetryMinutes));
        if (_modules.TryGetValue(key, out var existing))
        {
            if (existing.Module is not null) return existing;
            var recentlyFailed = DateTime.UtcNow - existing.FailedAtUtc < retry;
            if (recentlyFailed && (existing.TriedDownload || !allowDownload)) return existing;
        }

        var entry = existing ?? new ModuleSymbols();
        string? path = null;
        var downloaded = false;
        try
        {
            path = _local.FindSymbolFilePath(pdbName, guid, age, f.ModulePath);
            if (path is null && allowDownload)
            {
                path = _remote.FindSymbolFilePath(pdbName, guid, age, f.ModulePath);
                downloaded = path is not null;
                entry.TriedDownload = true;
            }
            if (path is not null) entry.Module = _local.OpenNativeSymbolFile(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Symbol lookup failed for {Pdb}", pdbName);
        }

        if (entry.Module is null)
        {
            entry.FailedAtUtc = DateTime.UtcNow;
            if (_reportedMissing.Add(key))
            {
                _metrics.SymbolsMissing.Add(1, new KeyValuePair<string, object?>("module", f.ModuleName));
                _logger.Log(_missingLevel, "No PDB found for {Module} ({Pdb} {Guid} age {Age}){Hint}",
                    f.ModuleName, pdbName, guid, age,
                    allowDownload ? "" : " - downloads not enabled for this process");
            }
        }
        else if (downloaded)
        {
            _metrics.SymbolsDownloaded.Add(1);
            _logger.LogInformation("Downloaded PDB for {Module}: {Path}", f.ModuleName, path);
        }

        _modules[key] = entry;
        return entry;
    }

    public override void Dispose()
    {
        foreach (var m in _modules.Values) m.Module?.Dispose();
        _local.Dispose();
        _remote.Dispose();
        base.Dispose();
    }
}

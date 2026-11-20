namespace WinProfAgent;

public sealed class AgentOptions
{
    public int WindowSeconds { get; set; } = 15;
    public int SampleHz { get; set; } = 100;
    public int BufferSizeMB { get; set; } = 64;
    public int QueueCapacity { get; set; } = 100;
    public int SnapshotQueueCapacity { get; set; } = 4;
    public int SessionRecycleWindows { get; set; } = 30;
    public bool CollectAllocations { get; set; } = true;
    public bool CollectContentions { get; set; } = true;

    // Native-capable profiles built from kernel events with stacks. Disk and network are cheap; wait time
    // (off-CPU: context switches) is high volume, so it is off by default.
    public bool CollectDisk { get; set; } = true;
    public bool CollectNetwork { get; set; } = true;
    public bool CollectWaits { get; set; } = false;
    public bool CollectThreadPool { get; set; } = true;
    public PyroscopeOptions Pyroscope { get; set; } = new();
    public ProcessFilter Processes { get; set; } = new();
    public SymbolOptions Symbols { get; set; } = new();
    public MetricsOptions Metrics { get; set; } = new();
}

public sealed class MetricsOptions
{
    public bool Enabled { get; set; } = true;
    public string? OtlpEndpoint { get; set; }
    public int ExportIntervalSeconds { get; set; } = 15;
}

public sealed class PyroscopeOptions
{
    public string Url { get; set; } = "http://localhost:4040";
    public string? BasicAuthUser { get; set; }
    public string? BasicAuthPassword { get; set; }
    public string? TenantId { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public Dictionary<string, string> Labels { get; set; } = new();
}

public sealed class ProcessFilter
{
    // Processes to profile. Empty means every process, named after the process, with symbol downloads disabled.
    public List<TargetOptions> Targets { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
}

public sealed class TargetOptions
{
    // Pyroscope service_name. Defaults to the process name.
    public string? Name { get; set; }

    // Process image name, with or without .exe. Matching is case-insensitive.
    public string Process { get; set; } = "";

    // Optional, case-insensitive text the process command line must contain. Lets several targets share one image,
    // e.g. dllhost.exe running different COM+ applications: use the AppID GUID from "/Processid:{GUID}".
    public string? CommandLineContains { get; set; }
}

public sealed class SymbolOptions
{
    // Local symbol store: searched first and used as the download cache. PDBs next to the profiled binary are always checked.
    public string SymbolFolder { get; set; } = @"C:\symbols";

    // Extra local folders searched before the symbol folder.
    public List<string> PdbSearchPaths { get; set; } = new();

    // Only applies to processes matching Processes:Targets; with an empty Targets list downloads are disabled.
    public bool AllowDownload { get; set; } = true;

    // Symbol servers in order of preference.
    public List<string> SymbolServers { get; set; } = new() { "https://msdl.microsoft.com/download/symbols" };

    // How long a module with no PDB is skipped before being looked up again.
    public int MissingRetryMinutes { get; set; } = 10;
}

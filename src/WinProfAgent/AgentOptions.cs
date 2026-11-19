namespace WinProfAgent;

public sealed class AgentOptions
{
    public int WindowSeconds { get; set; } = 60;
    public int SampleHz { get; set; } = 100;
    public PyroscopeOptions Pyroscope { get; set; } = new();
    public ProcessFilter Processes { get; set; } = new();
    public SymbolOptions Symbols { get; set; } = new();
}

public sealed class PyroscopeOptions
{
    public string Url { get; set; } = "http://localhost:4040";
    public string? BasicAuthUser { get; set; }
    public string? BasicAuthPassword { get; set; }
    public string? TenantId { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
}

public sealed class ProcessFilter
{
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
}

public sealed class SymbolOptions
{
    public List<string> PdbSearchPaths { get; set; } = new();
}

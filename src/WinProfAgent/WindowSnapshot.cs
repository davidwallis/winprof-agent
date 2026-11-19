using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace WinProfAgent;

public sealed class FrameInfo
{
    // Set when the name is already known (managed method, or raw address). Otherwise resolved from a PDB.
    public string? Name { get; set; }
    public string ModuleName { get; init; } = "";
    public string? ModulePath { get; init; }
    public string? PdbName { get; init; }
    public Guid PdbGuid { get; init; }
    public int PdbAge { get; init; }
    public uint Rva { get; init; }
}

public sealed class StackSample
{
    public required int[] Frames { get; init; } // leaf first, indexes into WindowSnapshot.Frames
    public long Count { get; set; }
    public long Value { get; set; } // bytes for allocations, nanoseconds for contention; unused for CPU
}

public sealed class ProcessSnapshot
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public List<StackSample> Stacks { get; } = new();
    public List<StackSample> AllocStacks { get; } = new();
    public List<StackSample> ContentionStacks { get; } = new();
    public List<StackSample> DiskStacks { get; } = new();    // Count = operations, Value = bytes
    public List<StackSample> NetworkStacks { get; } = new(); // Count = operations, Value = bytes
    public List<StackSample> WaitStacks { get; } = new();    // Count = waits, Value = nanoseconds
}

/// <summary>Everything needed to symbolize and encode a window, independent of the ETW TraceLog.</summary>
public sealed class WindowSnapshot
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset Until { get; init; }
    public required long PeriodNanos { get; init; }
    public required string Host { get; init; }
    public required bool AllowDownload { get; init; }
    public required FrameInfo[] Frames { get; init; }
    public required List<ProcessSnapshot> Processes { get; init; }
}

public sealed class SnapshotQueue
{
    private readonly Channel<WindowSnapshot> _channel;

    public SnapshotQueue(IOptions<AgentOptions> options, AgentMetrics metrics)
    {
        _channel = Channel.CreateBounded<WindowSnapshot>(
            new BoundedChannelOptions(Math.Max(1, options.Value.SnapshotQueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => metrics.WindowsDropped.Add(1));
    }

    public bool TryWrite(WindowSnapshot s) => _channel.Writer.TryWrite(s);

    public IAsyncEnumerable<WindowSnapshot> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

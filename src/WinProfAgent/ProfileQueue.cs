using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace WinProfAgent;

public sealed record ProfilePayload(
    string AppName,
    IReadOnlyDictionary<string, string> Labels,
    DateTimeOffset From,
    DateTimeOffset Until,
    byte[] Pprof);

public sealed class ProfileQueue
{
    private readonly Channel<ProfilePayload> _channel;

    public ProfileQueue(IOptions<AgentOptions> options)
    {
        _channel = Channel.CreateBounded<ProfilePayload>(
            new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Dropped?.Invoke());
    }

    public event Action? Dropped;

    public int Count => _channel.Reader.Count;

    public bool TryWrite(ProfilePayload p) => _channel.Writer.TryWrite(p);

    public IAsyncEnumerable<ProfilePayload> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

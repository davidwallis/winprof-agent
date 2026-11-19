using System.Text;

namespace WinProfAgent.Pprof;

/// <summary>Minimal pprof (profile.proto) encoder.</summary>
public sealed record SampleType(string Type, string Unit);

public sealed class PprofBuilder(IReadOnlyList<SampleType> sampleTypes, SampleType periodType)
{
    public static readonly SampleType[] CpuTypes = [new("cpu", "nanoseconds")];
    public static readonly SampleType CpuPeriod = new("cpu", "nanoseconds");

    public PprofBuilder() : this(CpuTypes, CpuPeriod) { }

    private readonly List<string> _strings = new() { "" };
    private readonly Dictionary<string, long> _stringIds = new() { [""] = 0 };
    private readonly Dictionary<string, ulong> _functionIds = new();
    private readonly List<string> _functionNames = new();
    private readonly Dictionary<string, int> _stacks = new();
    private readonly List<(ulong[] Locations, long[] Values)> _samples = new();

    public int SampleCount => _samples.Count;

    /// <param name="framesLeafFirst">Frame names, leaf first.</param>
    public void AddSample(IReadOnlyList<string> framesLeafFirst, params long[] values)
    {
        if (framesLeafFirst.Count == 0) return;

        var ids = new ulong[framesLeafFirst.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = FunctionId(framesLeafFirst[i]);

        var key = string.Join(',', ids);
        if (_stacks.TryGetValue(key, out var idx))
        {
            var s = _samples[idx];
            for (var i = 0; i < values.Length; i++) s.Values[i] += values[i];
        }
        else
        {
            _stacks[key] = _samples.Count;
            _samples.Add((ids, (long[])values.Clone()));
        }
    }

    public byte[] Build(DateTimeOffset start, TimeSpan duration, long period)
    {
        var nameIds = _functionNames.Select(StringId).ToArray();

        var w = new ProtoWriter();

        foreach (var st in sampleTypes)
        {
            var (type, unit) = (StringId(st.Type), StringId(st.Unit));
            w.Message(1, m => { m.Int64(1, type); m.Int64(2, unit); });
        }

        foreach (var (locations, values) in _samples)
        {
            w.Message(2, m =>
            {
                m.PackedUInt64(1, locations);
                m.PackedUInt64(2, values.Select(v => (ulong)v).ToArray());
            });
        }

        // Location id == function id, one line per location.
        for (var i = 0; i < nameIds.Length; i++)
        {
            var id = (ulong)(i + 1);
            w.Message(4, m =>
            {
                m.UInt64(1, id);
                m.Message(4, l => l.UInt64(1, id));
            });
        }

        for (var i = 0; i < nameIds.Length; i++)
        {
            var id = (ulong)(i + 1);
            var name = nameIds[i];
            w.Message(5, m =>
            {
                m.UInt64(1, id);
                m.Int64(2, name);
                m.Int64(3, name);
            });
        }

        foreach (var s in _strings) w.String(6, s);

        w.Int64(9, start.ToUnixTimeMilliseconds() * 1_000_000);
        w.Int64(10, duration.Ticks * 100);
        var (pType, pUnit) = (StringId(periodType.Type), StringId(periodType.Unit));
        w.Message(11, m => { m.Int64(1, pType); m.Int64(2, pUnit); });
        w.Int64(12, period);

        return w.ToArray();
    }

    private ulong FunctionId(string name)
    {
        if (_functionIds.TryGetValue(name, out var id)) return id;
        _functionNames.Add(name);
        id = (ulong)_functionNames.Count;
        _functionIds[name] = id;
        return id;
    }

    private long StringId(string s)
    {
        if (_stringIds.TryGetValue(s, out var id)) return id;
        id = _strings.Count;
        _strings.Add(s);
        _stringIds[s] = id;
        return id;
    }

    private sealed class ProtoWriter
    {
        private readonly MemoryStream _ms = new();

        public byte[] ToArray() => _ms.ToArray();

        public void Message(int field, Action<ProtoWriter> body)
        {
            var inner = new ProtoWriter();
            body(inner);
            Tag(field, 2);
            Varint((ulong)inner._ms.Length);
            inner._ms.WriteTo(_ms);
        }

        public void String(int field, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Tag(field, 2);
            Varint((ulong)bytes.Length);
            _ms.Write(bytes);
        }

        public void Int64(int field, long value)
        {
            Tag(field, 0);
            Varint((ulong)value);
        }

        public void UInt64(int field, ulong value)
        {
            Tag(field, 0);
            Varint(value);
        }

        public void PackedUInt64(int field, ulong[] values)
        {
            var inner = new ProtoWriter();
            foreach (var v in values) inner.Varint(v);
            Tag(field, 2);
            Varint((ulong)inner._ms.Length);
            inner._ms.WriteTo(_ms);
        }

        private void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

        private void Varint(ulong v)
        {
            while (v >= 0x80)
            {
                _ms.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            _ms.WriteByte((byte)v);
        }
    }
}

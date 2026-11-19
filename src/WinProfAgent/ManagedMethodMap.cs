namespace WinProfAgent;

/// <summary>
/// Per-process table of JIT-compiled method ranges, filled from CLR load and rundown events so methods of
/// processes that started before the agent can still be named.
/// </summary>
internal sealed class ManagedMethodMap
{
    private sealed class Table
    {
        public readonly List<Entry> Entries = new();
        public bool Sorted = true;
    }

    private readonly record struct Entry(ulong Start, ulong End, string Name);

    private readonly Dictionary<int, Table> _byPid = new();

    public int Count { get; private set; }

    public void Add(int pid, ulong start, int size, string ns, string name, string signature)
    {
        if (start == 0 || size <= 0 || string.IsNullOrEmpty(name)) return;

        if (!_byPid.TryGetValue(pid, out var table)) _byPid[pid] = table = new Table();
        table.Entries.Add(new Entry(start, start + (ulong)size, Format(ns, name, signature)));
        table.Sorted = false;
        Count++;
    }

    public string? Lookup(int pid, ulong address)
    {
        if (!_byPid.TryGetValue(pid, out var table) || table.Entries.Count == 0) return null;

        if (!table.Sorted)
        {
            table.Entries.Sort((a, b) => a.Start.CompareTo(b.Start));
            table.Sorted = true;
        }

        var entries = table.Entries;
        int lo = 0, hi = entries.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (entries[mid].Start <= address) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        return found >= 0 && address < entries[found].End ? entries[found].Name : null;
    }

    // Signatures look like "void  (int32,class System.String)"; keep just the parameter list, as TraceEvent does.
    private static string Format(string ns, string name, string signature)
    {
        var paren = signature.IndexOf('(');
        var args = paren >= 0 ? signature[paren..] : "()";
        return string.IsNullOrEmpty(ns) ? name + args : $"{ns}.{name}{args}";
    }
}

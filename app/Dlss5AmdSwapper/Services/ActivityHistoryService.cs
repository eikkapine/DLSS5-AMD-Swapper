using System.Text.Json;

namespace Dlss5AmdSwapper.Services;

public sealed record ActivityEntry(DateTimeOffset Time, string Action, string Detail);

/// <summary>Private, bounded local history. Never included in exported diagnostics.</summary>
public sealed class ActivityHistoryService
{
    public const int EntryLimit = 200;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<ActivityEntry> _entries = [];

    public ActivityHistoryService(string? folder = null)
    {
        _path = Path.Combine(folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5 AMD Swapper"), "activity.json");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= 2 * 1024 * 1024)
                _entries.AddRange((JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(_path)) ?? [])
                    .Where(e => e is not null).TakeLast(EntryLimit)
                    .Select(e => new ActivityEntry(e.Time, Limit(e.Action, 80), Limit(e.Detail, 600))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public IReadOnlyList<ActivityEntry> Read() { lock (_gate) return _entries.AsEnumerable().Reverse().ToArray(); }

    public void Record(string action, string detail = "")
    {
        lock (_gate)
        {
            _entries.Add(new(DateTimeOffset.Now, Limit(action, 80), Limit(detail, 600)));
            if (_entries.Count > EntryLimit) _entries.RemoveRange(0, _entries.Count - EntryLimit);
            Save();
        }
    }

    public void Clear() { lock (_gate) { _entries.Clear(); Save(); } }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Local history must not interrupt a game action. */ }
    }

    private static string Limit(string? text, int length) => string.IsNullOrEmpty(text) ? string.Empty : text[..Math.Min(text.Length, length)];
}

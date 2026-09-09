using System.Globalization;
using System.Text;

namespace Dlss5AmdSwapper.Services;

public sealed class IniDocument
{
    private readonly List<string> _lines;

    private IniDocument(List<string> lines) => _lines = lines;

    public static IniDocument Load(string path)
    {
        return new IniDocument(File.Exists(path) ? File.ReadAllLines(path).ToList() : []);
    }

    public string? Get(string section, string key)
    {
        var active = string.Empty;
        foreach (var raw in _lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                active = line[1..^1].Trim();
                continue;
            }
            if (!active.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var split = raw.IndexOf('=');
            if (split <= 0) continue;
            if (raw[..split].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return raw[(split + 1)..].Trim();
        }
        return null;
    }

    public bool GetBool(string section, string key, bool fallback)
    {
        return Get(section, key)?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    public double GetDouble(string section, string key, double fallback)
    {
        return double.TryParse(Get(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    public void Set(string section, string key, string value)
    {
        var sectionIndex = FindSection(section);
        if (sectionIndex < 0)
        {
            if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1])) _lines.Add(string.Empty);
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        var end = _lines.Count;
        for (var i = sectionIndex + 1; i < _lines.Count; i++)
        {
            var trimmed = _lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { end = i; break; }
            var split = _lines[i].IndexOf('=');
            if (split > 0 && _lines[i][..split].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{key}={value}";
                return;
            }
        }
        _lines.Insert(end, $"{key}={value}");
    }

    public void SaveAtomic(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(temp, _lines, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private int FindSection(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']') && line[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}

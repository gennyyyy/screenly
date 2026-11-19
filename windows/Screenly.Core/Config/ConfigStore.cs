using System.Text.Json;

namespace Screenly.Core.Config;

public static class ConfigStore
{
    public static string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Screenly", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ConfigStore.Load failed: {ex.Message}");
            return new AppConfig();
        }
    }

    public static void Save(AppConfig cfg, string? path = null)
    {
        var p = path ?? ConfigPath;
        var dir = Path.GetDirectoryName(p)!;
        Directory.CreateDirectory(dir);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOpts));
        if (File.Exists(p)) File.Replace(tmp, p, p + ".bak");
        else File.Move(tmp, p);
    }

    /// <summary>
    /// Codec currently on disk, lowercased, or null when the file is missing/unreadable.
    /// Unlike <see cref="Load"/> it never falls back to defaults, so a transient read error
    /// cannot be mistaken for a real "jpeg" selection by a change poller.
    /// </summary>
    public static string? ReadCodec(string? path = null)
    {
        try
        {
            var p = path ?? ConfigPath;
            if (!File.Exists(p)) return null;
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(p), JsonOpts);
            return (cfg?.Encoding?.Codec ?? "jpeg").ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// Target fps currently on disk, or null when the file is missing/unreadable.
    /// Like <see cref="ReadCodec"/> it never falls back to defaults, so a transient read error
    /// cannot look like a real fps change to the change poller.
    /// </summary>
    public static int? ReadTargetFps(string? path = null)
    {
        try
        {
            var p = path ?? ConfigPath;
            if (!File.Exists(p)) return null;
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(p), JsonOpts);
            return cfg?.Encoding?.TargetFps;
        }
        catch { return null; }
    }

    public static string ResolutionsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Screenly", "vdd-resolutions.json");

    private sealed record PersistedResolution(int Width, int Height);

    public static Dictionary<string, (int w, int h)> LoadPersistedResolutions(string? path = null)
    {
        var p = path ?? ResolutionsPath;
        var result = new Dictionary<string, (int w, int h)>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(p)) return result;
            var json = File.ReadAllText(p);
            var map = JsonSerializer.Deserialize<Dictionary<string, PersistedResolution>>(json, JsonOpts);
            if (map == null) return result;
            foreach (var kv in map)
            {
                if (kv.Value is { Width: > 0, Height: > 0 })
                    result[kv.Key] = (kv.Value.Width, kv.Value.Height);
            }
        }
        catch { /* invalid file -> empty */ }
        return result;
    }

    public static void SavePersistedResolutions(Dictionary<string, (int w, int h)> resolutions, string? path = null)
    {
        var p = path ?? ResolutionsPath;
        var dir = Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var map = new Dictionary<string, PersistedResolution>();
        foreach (var kv in resolutions)
        {
            if (kv.Value.w > 0 && kv.Value.h > 0)
                map[kv.Key] = new PersistedResolution(kv.Value.w, kv.Value.h);
        }
        File.WriteAllText(p, JsonSerializer.Serialize(map, JsonOpts));
    }

    public static (int Width, int Height) CapResolution(int clientWidth, int clientHeight, int maxWidth, int maxHeight)
        => ApplyResolutionCap(clientWidth, clientHeight, maxWidth, maxHeight);

    public static (int Width, int Height) ApplyResolutionCap(int clientWidth, int clientHeight, int maxWidth, int maxHeight)
        => ScaleToCap(clientWidth, clientHeight, maxWidth, maxHeight);

    public static (int Width, int Height) ScaleToCap(int clientWidth, int clientHeight, int maxWidth, int maxHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0) return (clientWidth, clientHeight);
        if (clientWidth <= maxWidth && clientHeight <= maxHeight)
            return (clientWidth, clientHeight);
        double scale = Math.Min((double)maxWidth / clientWidth, (double)maxHeight / clientHeight);
        int w = (int)Math.Round(clientWidth * scale);
        int h = (int)Math.Round(clientHeight * scale);
        if (w % 2 != 0) w = Math.Max(2, w - 1);
        if (h % 2 != 0) h = Math.Max(2, h - 1);
        if (w > maxWidth) w -= 2;
        if (h > maxHeight) h -= 2;
        if (w < 2) w = 2;
        if (h < 2) h = 2;
        return (w, h);
    }

    public static (int Width, int Height) GetCappedResolution(int clientWidth, int clientHeight, int maxWidth, int maxHeight)
        => ScaleToCap(clientWidth, clientHeight, maxWidth, maxHeight);
}
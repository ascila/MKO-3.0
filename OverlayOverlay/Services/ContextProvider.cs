using System;
using System.IO;
using System.Text.Json;
using OverlayOverlay.Models;

namespace OverlayOverlay.Services;

public static class ContextProvider
{
    private static readonly object _lock = new();
    private static SetupContext _cache = new();
    private static bool _loaded;

    public static string SessionName { get; private set; } = "default";

    public static void SetSession(string name)
    {
        lock (_lock)
        {
            SessionName = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
            _loaded = false; // force reload with new session
        }
    }

    private static string GetRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(root, "OverlayOverlay");
        // Root app folder can be created immediately
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string GetSessionsRoot()
    {
        // Do not create session folders implicitly when browsing/reading
        return Path.Combine(GetRoot(), "Sessions");
    }

    private static string GetSessionFolder() => Path.Combine(GetSessionsRoot(), SessionName);

    // Expose current session folder path without side effects
    public static string GetSessionFolderPath() => GetSessionFolder();

    private static string GetPath() => Path.Combine(GetSessionFolder(), "SetupContext.json");

    public static SetupContext Load()
    {
        lock (_lock)
        {
            if (_loaded) return _cache;
            try
            {
                var path = GetPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var ctx = JsonSerializer.Deserialize<SetupContext>(json) ?? new SetupContext();
                    _cache = ctx;
                }
            }
            catch { _cache = new SetupContext(); }
            finally { _loaded = true; }
            return _cache;
        }
    }

    public static SetupContext Get()
    {
        if (!_loaded) Load();
        return _cache;
    }

    public static void Save(SetupContext ctx)
    {
        lock (_lock)
        {
            _cache = ctx;
            _cache.UpdatedAt = DateTime.UtcNow;
            try
            {
                // Ensure folders exist only when saving (session creation point)
                Directory.CreateDirectory(GetSessionsRoot());
                Directory.CreateDirectory(GetSessionFolder());
                var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetPath(), json);
            }
            catch { }
        }
    }
}

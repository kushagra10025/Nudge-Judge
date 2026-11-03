using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Config;

public interface IConfigStore
{
    /// <summary>The live configuration. Mutate through <see cref="Update"/> so changes persist and notify.</summary>
    AppConfig Current { get; }

    /// <summary>Set when the file on disk couldn't be read and defaults were used instead.</summary>
    string? LoadError { get; }

    /// <summary>Set while the latest changes aren't on disk (the last write failed, or saving is off). Cleared by a good save.</summary>
    string? SaveError => null;

    /// <summary>Applies <paramref name="mutate"/> to <see cref="Current"/> and persists it. Never throws for disk problems; see <see cref="SaveError"/>.</summary>
    void Update(Action<AppConfig> mutate);

    /// <summary>Raised after every <see cref="Update"/>, possibly on a background thread.</summary>
    event EventHandler? Changed;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>
/// JSON-file backed config. Thread-safe within a process, and safe against other NudgeJudge processes:
/// each update holds <c>config.json.lock</c> and, if someone else rewrote the file since we last saw it,
/// re-reads it first, so their changes aren't overwritten. Writes are atomic (temp file + rename) and owner-only.
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    private readonly string _path;
    private readonly string _lockPath;
    private readonly Lock _gate = new();

    /// <summary>Hash of the file as we last read or wrote it; null when we haven't seen a readable file.</summary>
    private byte[]? _diskHash;

    /// <summary>Memory holds changes the file doesn't (a failed write). They win over a newer file and are retried.</summary>
    private bool _unsaved;

    /// <summary>The file came from a newer NudgeJudge. Nothing is written, so it isn't silently downgraded.</summary>
    private bool _readOnly;

    public JsonConfigStore(string path, Func<AppConfig>? seed = null)
    {
        _path = path;
        _lockPath = path + ".lock";
        Current = Load(seed);
    }

    public AppConfig Current { get; private set; }
    public string? LoadError { get; private set; }
    public string? SaveError { get; private set; }
    public event EventHandler? Changed;

    public void Update(Action<AppConfig> mutate)
    {
        lock (_gate)
        {
            FileLock? fileLock = null;
            try
            {
                // Can't get the lock in time: carry on without it rather than freeze the UI. The write is still atomic.
                try { fileLock = FileLock.Acquire(_lockPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

                if (!_unsaved && !_readOnly) PickUpExternalChanges();
                mutate(Current);
                TrySave();
            }
            finally
            {
                fileLock?.Dispose();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppConfig Load(Func<AppConfig>? seed)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(_path))
            {
                Current = seed?.Invoke() ?? new AppConfig();
                TrySave();
                return Current;
            }
            bytes = ReadShared(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadError = $"Couldn't read {_path}: {ex.Message}. Using defaults.";
            return new AppConfig();
        }

        _diskHash = SHA256.HashData(bytes);
        if (!TryParse(bytes, out var config, out var error))
        {
            var backup = KeepCopy(bytes, "broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            LoadError = $"Couldn't read {_path}: {error}. Using defaults."
                        + (backup is null ? "" : $" The unreadable file was kept as {Path.GetFileName(backup)}.");
            return new AppConfig();
        }

        if (config.Version > AppConfig.CurrentVersion)
        {
            _readOnly = true;
            LoadError = $"{Path.GetFileName(_path)} was saved by a newer version of NudgeJudge. "
                        + "Changes you make here won't be saved, so that version's settings stay intact.";
            return config;
        }

        var fromVersion = config.Version;
        if (config.Upgrade())
        {
            // Keep the old shape until the upgrade is on disk, so going back a version stays possible.
            KeepCopy(bytes, $"v{fromVersion}.bak");
            Current = config;
            TrySave();
        }
        return config;
    }

    /// <summary>Another process rewrote the file since we last read or wrote it: start from its version.</summary>
    private void PickUpExternalChanges()
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(_path)) return;
            bytes = ReadShared(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var hash = SHA256.HashData(bytes);
        if (_diskHash is not null && hash.AsSpan().SequenceEqual(_diskHash)) return;
        // Unreadable or from a newer version: keep what we have, and our next save replaces it as before.
        if (!TryParse(bytes, out var config, out _) || config.Version > AppConfig.CurrentVersion) return;

        config.Upgrade();
        Current = config;
        _diskHash = hash;
        // A previous load failure no longer describes what's in memory.
        LoadError = null;
    }

    /// <summary>Reads while letting another process's atomic rename replace the file (Windows needs delete sharing for that).</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool TryParse(byte[] bytes, out AppConfig config, out string? error)
    {
        try
        {
            config = JsonSerializer.Deserialize(bytes, ConfigJsonContext.Default.AppConfig) ?? new AppConfig();
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            config = new AppConfig();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Saves a copy of <paramref name="bytes"/> next to the config, without replacing an existing one. Returns its path.</summary>
    private string? KeepCopy(byte[] bytes, string suffix)
    {
        var backup = _path + "." + suffix;
        try
        {
            if (!File.Exists(backup)) AppPaths.WritePrivateFileAtomic(backup, bytes);
            return backup;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TrySave()
    {
        if (_readOnly)
        {
            _unsaved = true;
            SaveError = "Changes aren't being saved: the config file is from a newer version of NudgeJudge.";
            return;
        }

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Current, ConfigJsonContext.Default.AppConfig);
            AppPaths.WritePrivateFileAtomic(_path, bytes);
            _diskHash = SHA256.HashData(bytes);
            _unsaved = false;
            SaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Memory keeps the change and the next update tries again.
            _unsaved = true;
            SaveError = "Couldn't save your changes: " + ex.Message;
        }
    }
}

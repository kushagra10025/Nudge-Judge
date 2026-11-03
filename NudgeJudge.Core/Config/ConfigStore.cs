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

    void Update(Action<AppConfig> mutate);
    event EventHandler? Changed;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>JSON-file backed config. Thread-safe; writes are atomic and owner-only.</summary>
public sealed class JsonConfigStore : IConfigStore
{
    private readonly string _path;
    private readonly Lock _gate = new();

    public JsonConfigStore(string path, Func<AppConfig>? seed = null)
    {
        _path = path;
        Current = Load(seed);
    }

    public AppConfig Current { get; private set; }
    public string? LoadError { get; private set; }
    public event EventHandler? Changed;

    public void Update(Action<AppConfig> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppConfig Load(Func<AppConfig>? seed)
    {
        if (!File.Exists(_path))
        {
            var fresh = seed?.Invoke() ?? new AppConfig();
            Current = fresh;
            TrySave();
            return fresh;
        }

        try
        {
            var bytes = File.ReadAllBytes(_path);
            return JsonSerializer.Deserialize(bytes, ConfigJsonContext.Default.AppConfig) ?? new AppConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the broken file for the user to inspect; don't overwrite it until they change something.
            LoadError = $"Couldn't read {_path}: {ex.Message}. Using defaults.";
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch (IOException) { }
            return new AppConfig();
        }
    }

    private void TrySave()
    {
        try { Save(); }
        catch (IOException ex) { LoadError = "Couldn't write config: " + ex.Message; }
        catch (UnauthorizedAccessException ex) { LoadError = "Couldn't write config: " + ex.Message; }
    }

    private void Save() =>
        AppPaths.WritePrivateFileAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(Current, ConfigJsonContext.Default.AppConfig));
}

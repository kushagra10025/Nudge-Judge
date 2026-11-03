using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NudgeJudge.Core.Config;

namespace NudgeJudge.Core.Routing;

/// <summary>Where <see cref="ActivityLog"/> keeps its entries between runs.</summary>
public interface IActivityStore
{
    /// <summary>All stored entries, oldest first.</summary>
    IReadOnlyList<ActivityEntry> Read();

    /// <summary>Reads, applies <paramref name="change"/> and writes back as one step no other process can interleave with.</summary>
    IReadOnlyList<ActivityEntry> Update(Action<List<ActivityEntry>> change);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActivityEntry))]
internal sealed partial class ActivityJsonContext : JsonSerializerContext;

/// <summary>
/// Rolling activity log on disk: one JSON object per line, oldest first, readable only by the current user.
/// Normally only the primary instance writes it, but a standalone launch (single-instance unavailable) can too,
/// so each update holds <c>activity.log.lock</c> while it reads, changes and atomically replaces the file.
/// A crash mid-write leaves the previous file intact. Lines that can't be parsed (hand edits) are skipped.
/// </summary>
public sealed class ActivityLogFile(string path) : IActivityStore
{
    public string Path { get; } = path;

    private string LockPath => Path + ".lock";

    public IReadOnlyList<ActivityEntry> Read()
    {
        // Every write is a whole-file rename, so a plain read always sees one complete version.
        return File.Exists(Path) ? Parse(ReadText()) : [];
    }

    public IReadOnlyList<ActivityEntry> Update(Action<List<ActivityEntry>> change)
    {
        using var held = FileLock.Acquire(LockPath);
        var before = File.Exists(Path) ? Parse(ReadText()) : [];
        var after = new List<ActivityEntry>(before);
        change(after);
        if (after.SequenceEqual(before)) return after;

        var text = new StringBuilder();
        foreach (var entry in after)
            text.Append(JsonSerializer.Serialize(entry, ActivityJsonContext.Default.ActivityEntry)).Append('\n');
        AppPaths.WritePrivateFileAtomic(Path, Encoding.UTF8.GetBytes(text.ToString()));
        return after;
    }

    /// <summary>Shares delete access so a writer's rename can replace the file while we read it (matters on Windows).</summary>
    private string ReadText()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static List<ActivityEntry> Parse(string text)
    {
        var entries = new List<ActivityEntry>();
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, ActivityJsonContext.Default.ActivityEntry) is { Url: not null } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
            }
        }
        return entries;
    }
}

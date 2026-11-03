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
/// Every link launch is currently its own process, so each update holds an exclusive lock on the file
/// while it reads, trims and rewrites it. Lines that can't be parsed (a torn write, hand edits) are skipped.
/// </summary>
public sealed class ActivityLogFile(string path) : IActivityStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

    public string Path { get; } = path;

    public IReadOnlyList<ActivityEntry> Read()
    {
        if (!File.Exists(Path)) return [];
        using var stream = Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        return Parse(stream);
    }

    public IReadOnlyList<ActivityEntry> Update(Action<List<ActivityEntry>> change)
    {
        AppPaths.EnsurePrivateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var stream = Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var before = Parse(stream);
        var after = new List<ActivityEntry>(before);
        change(after);
        if (after.SequenceEqual(before)) return after;

        var text = new StringBuilder();
        foreach (var entry in after)
            text.Append(JsonSerializer.Serialize(entry, ActivityJsonContext.Default.ActivityEntry)).Append('\n');

        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
        stream.Flush(flushToDisk: true);
        return after;
    }

    private static List<ActivityEntry> Parse(Stream stream)
    {
        var entries = new List<ActivityEntry>();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (reader.ReadLine() is { } line)
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

    private FileStream Open(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows() && mode == FileMode.OpenOrCreate)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // Another NudgeJudge process may hold the lock for the few milliseconds an update takes.
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(Path, options);
            }
            catch (IOException) when (DateTime.UtcNow < deadline && File.Exists(Path))
            {
                Thread.Sleep(15);
            }
        }
    }
}

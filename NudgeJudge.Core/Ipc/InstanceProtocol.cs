using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Security;

namespace NudgeJudge.Core.Ipc;

public enum InstanceCommand
{
    /// <summary>Route the attached URLs.</summary>
    Open,
    /// <summary>Bring the main window forward (the app was launched again without links).</summary>
    Show,
}

/// <param name="Source">
/// The app that opened the links, as seen by the launch that received them (only that process can tell).
/// Optional and untrusted: the receiver sanitizes it before use.
/// </param>
public sealed record InstanceRequest(int Version, string Token, InstanceCommand Command, IReadOnlyList<string> Urls,
    SourceApp? Source = null);

public sealed record InstanceReply(bool Ok, string? Error = null)
{
    public static InstanceReply Success { get; } = new(true);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstanceRequest))]
[JsonSerializable(typeof(InstanceReply))]
internal sealed partial class InstanceJsonContext : JsonSerializerContext;

/// <summary>
/// Wire format between launches: a 4-byte big-endian length, then that many bytes of UTF-8 JSON.
/// One request and one reply per connection.
/// </summary>
public static class InstanceProtocol
{
    public const int Version = 1;
    public const int MaxUrls = 64;

    /// <summary>Reply to a request with the wrong token. Such a request was never acted on, so the sender may retry.</summary>
    public const string UnauthorizedError = "Not authorized.";

    /// <summary>Generous for <see cref="MaxUrls"/> URLs of <see cref="UrlValidator.MaxLength"/> each, plus JSON escaping.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Constant-time token comparison.</summary>
    public static bool TokensMatch(string? expected, string? actual)
    {
        if (expected is null || actual is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    public static Task WriteRequestAsync(Stream stream, InstanceRequest request, CancellationToken ct) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request, InstanceJsonContext.Default.InstanceRequest), ct);

    public static Task WriteReplyAsync(Stream stream, InstanceReply reply, CancellationToken ct) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(reply, InstanceJsonContext.Default.InstanceReply), ct);

    /// <summary>Reads a request, or returns null if the frame is malformed in any way.</summary>
    public static async Task<InstanceRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var frame = await ReadFrameAsync(stream, ct);
        if (frame is null) return null;
        try
        {
            var request = JsonSerializer.Deserialize(frame, InstanceJsonContext.Default.InstanceRequest);
            return request is { Token: not null, Urls: not null } && request.Urls.All(u => u is not null) ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<InstanceReply?> ReadReplyAsync(Stream stream, CancellationToken ct)
    {
        var frame = await ReadFrameAsync(stream, ct);
        if (frame is null) return null;
        try
        {
            return JsonSerializer.Deserialize(frame, InstanceJsonContext.Default.InstanceReply);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Checks everything about a request except the token. Returns an error, or null when it's acceptable.</summary>
    public static string? Check(InstanceRequest request)
    {
        if (request.Version != Version) return $"Unsupported protocol version {request.Version}.";
        if (!Enum.IsDefined(request.Command)) return "Unknown command.";
        if (request.Urls.Count > MaxUrls) return $"More than {MaxUrls} links in one message.";
        if (request.Command == InstanceCommand.Show && request.Urls.Count > 0) return "Show takes no links.";
        return null;
    }

    internal static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrameBytes) throw new InvalidOperationException("Message is too large.");
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length);
        payload.CopyTo(buffer, 4);
        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Returns the frame's payload, or null on a bad length or early end of stream.</summary>
    internal static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length <= 0 || length > MaxFrameBytes) return null;
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, ct);
            return payload;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}

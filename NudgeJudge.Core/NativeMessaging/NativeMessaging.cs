using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NudgeJudge.Core.NativeMessaging;

/// <summary>A message from the browser extension. See docs/browser-extension.md#protocol.</summary>
/// <param name="Auto">With <c>open</c>: sent by automatic routing rather than a gesture of the user's.</param>
public sealed record NativeRequest(int V, string? Type, string? Url = null, bool Auto = false);

/// <summary>The reply to a <see cref="NativeRequest"/>.</summary>
public sealed record NativeReply(int V, bool Ok, string? Action = null, string? Error = null, string? App = null,
    IReadOnlyList<string>? Hosts = null)
{
    public static NativeReply Fail(string error) => new(NativeHost.ProtocolVersion, false, Error: error);
    public static NativeReply Done(string action) => new(NativeHost.ProtocolVersion, true, action);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NativeRequest))]
[JsonSerializable(typeof(NativeReply))]
internal sealed partial class NativeJsonContext : JsonSerializerContext;

/// <summary>
/// Native messaging framing: a 4-byte length in the machine's own byte order, then that many bytes of UTF-8 JSON.
/// (Unlike <see cref="Ipc.InstanceProtocol"/>, which is big-endian: that format is ours, this one is the browsers'.)
/// </summary>
public static class NativeMessageFraming
{
    /// <summary>Generous for one URL of <see cref="Security.UrlValidator.MaxLength"/> plus JSON escaping.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>The browsers' limit on a message to the extension.</summary>
    public const int MaxReplyBytes = 1024 * 1024;

    /// <summary>The next message, or null at a clean end of input (the extension disconnected).</summary>
    /// <exception cref="InvalidDataException">A bad length, or the input ended mid-message.</exception>
    public static byte[]? Read(Stream input)
    {
        var header = new byte[4];
        var got = ReadUpTo(input, header);
        if (got == 0) return null;
        if (got < 4) throw new InvalidDataException("The message ended early.");

        var length = BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(header)
            : BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxRequestBytes) throw new InvalidDataException($"Bad message length {length}.");

        var payload = new byte[length];
        if (ReadUpTo(input, payload) < length) throw new InvalidDataException("The message ended early.");
        return payload;
    }

    public static void Write(Stream output, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxReplyBytes) throw new InvalidOperationException("Reply is too large.");
        Span<byte> header = stackalloc byte[4];
        if (BitConverter.IsLittleEndian) BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        else BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        output.Write(header);
        output.Write(payload);
        output.Flush();
    }

    private static int ReadUpTo(Stream input, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = input.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>What <c>open</c> did with a link.</summary>
public enum NativeOpenOutcome
{
    /// <summary>The rules pick the browser it came from: the extension should let it open there.</summary>
    Stay,
    /// <summary>NudgeJudge has it.</summary>
    Sent,
}

/// <summary>What the native host needs from the rest of the app. Faked in tests.</summary>
public interface INativeHostBackend
{
    string AppVersion { get; }

    /// <summary>Decides and acts on a validated link. Throws <see cref="IOException"/> when it couldn't be handed over.</summary>
    NativeOpenOutcome Open(Uri url, bool auto);

    /// <summary>Host globs of the rules marked "Also catch in the browser".</summary>
    IReadOnlyList<string> AutoRouteHosts();
}

/// <summary>
/// Serves one native messaging session: the browser starts the app with <c>--native-host</c> and talks over
/// stdin/stdout until it closes stdin. <c>sendNativeMessage</c> sends one message; <c>connectNative</c> can send many.
/// </summary>
public static class NativeHost
{
    public const int ProtocolVersion = 1;

    /// <summary>The name the extension connects to, and the host manifest's file name.</summary>
    public const string Name = "com.nudgejudge.host";

    /// <summary>The command-line flag the launcher passes.</summary>
    public const string Flag = "--native-host";

    /// <summary>Answers messages until the input ends or a frame is malformed. Never throws for bad input.</summary>
    public static void Serve(Stream input, Stream output, INativeHostBackend backend)
    {
        while (true)
        {
            byte[]? frame;
            try
            {
                frame = NativeMessageFraming.Read(input);
            }
            catch (InvalidDataException e)
            {
                // The stream is out of step now: say why, then stop.
                Reply(output, NativeReply.Fail(e.Message));
                return;
            }
            if (frame is null) return;
            Reply(output, Handle(frame, backend));
        }
    }

    internal static NativeReply Handle(byte[] frame, INativeHostBackend backend)
    {
        NativeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(frame, NativeJsonContext.Default.NativeRequest);
        }
        catch (JsonException)
        {
            return NativeReply.Fail("Not a valid message.");
        }
        if (request is null) return NativeReply.Fail("Not a valid message.");
        if (request.V != ProtocolVersion)
            return NativeReply.Fail($"Unsupported protocol version {request.V}. Update NudgeJudge and the extension.");

        try
        {
            switch (request.Type)
            {
                case "ping":
                    return new NativeReply(ProtocolVersion, true, "pong", App: backend.AppVersion);
                case "autoRoutes":
                    return new NativeReply(ProtocolVersion, true, "autoRoutes", Hosts: backend.AutoRouteHosts());
                case "open":
                    var validation = Security.UrlValidator.Validate(request.Url);
                    if (!validation.IsValid) return NativeReply.Fail(validation.Error ?? "Not a link NudgeJudge can open.");
                    return NativeReply.Done(backend.Open(validation.Url!, request.Auto) == NativeOpenOutcome.Stay ? "stay" : "sent");
                default:
                    return NativeReply.Fail("Unknown message type.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return NativeReply.Fail(e.Message);
        }
    }

    private static void Reply(Stream output, NativeReply reply) =>
        NativeMessageFraming.Write(output, JsonSerializer.SerializeToUtf8Bytes(reply, NativeJsonContext.Default.NativeReply));
}

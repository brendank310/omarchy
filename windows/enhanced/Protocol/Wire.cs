using System.Buffers.Binary;
using System.Text.Json;

namespace Omarchy.Enhanced;

// One bounded JSON object per little-endian length-prefixed frame. The same
// framing is used on the host channel and the service's local agent pipe.
public sealed class Wire(Stream stream)
{
  public const int MaxFrame = 1024 * 1024;
  readonly SemaphoreSlim writeLock = new(1);
  static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

  public async Task<JsonDocument?> Read(CancellationToken cancel)
  {
    var header = new byte[4];
    int first = await stream.ReadAsync(header.AsMemory(0, 1), cancel);
    if (first == 0) return null;
    await stream.ReadExactlyAsync(header.AsMemory(1), cancel);
    uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (length == 0 || length > MaxFrame) throw new InvalidDataException("Invalid frame length");
    var data = new byte[(int)length];
    await stream.ReadExactlyAsync(data, cancel);
    var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 24 });
    if (document.RootElement.ValueKind != JsonValueKind.Object)
    {
      document.Dispose();
      throw new InvalidDataException("Expected JSON object");
    }
    return document;
  }

  public async Task Write<T>(T value, CancellationToken cancel)
  {
    byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, Json);
    if (data.Length > MaxFrame) throw new InvalidDataException("Frame exceeds limit");
    byte[] header = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
    await writeLock.WaitAsync(cancel);
    try
    {
      await stream.WriteAsync(header, cancel);
      await stream.WriteAsync(data, cancel);
      await stream.FlushAsync(cancel);
    }
    finally { writeLock.Release(); }
  }
}

public sealed record ApplicationEntry(string Id, string Name, string Executable);
public sealed record WindowInfo(string Id, uint ProcessId, string Title, string ClassName,
  int X, int Y, int Width, int Height, bool Minimized, bool Maximized, bool Foreground, uint Dpi,
  string? OwnerId, int ZOrder)
{
  public WindowFrame? Frame { get; init; }
}
public sealed record WindowFrame(int X, int Y, int Width, int Height);

public static class Requests
{
  public static bool MatchesSession(JsonElement root, string expected) =>
    root.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.String &&
    session.GetString() == expected;

  public static (string Id, string Method) Parse(JsonElement root)
  {
    if (!root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != 1)
      throw new InvalidDataException("Unsupported protocol version");
    if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 128)
      throw new InvalidDataException("Invalid request ID");
    if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
      throw new InvalidDataException("Missing method");
    return (id.GetString()!, method.GetString()!);
  }

  public static ApplicationEntry ResolveLaunch(JsonElement root, IReadOnlyList<ApplicationEntry> catalog)
  {
    if (!root.TryGetProperty("appId", out var id) || id.ValueKind != JsonValueKind.String)
      throw new InvalidDataException("Missing application ID");
    // No host-provided executable paths, arguments, shell verbs, or scripts.
    if (root.EnumerateObject().Any(p => p.Name is not ("v" or "id" or "method" or "appId" or "session")))
      throw new InvalidDataException("Unexpected launch field");
    return catalog.SingleOrDefault(app => app.Id == id.GetString()) ??
      throw new InvalidDataException("Unknown application ID");
  }
}

using System.Buffers.Binary;
using System.Text.Json;
using Omarchy.Enhanced;

static void Check(bool value, string description)
{
  if (!value) throw new Exception(description);
  Console.WriteLine($"ok - {description}");
}

static async Task Reject(Func<Task> action, string description)
{
  try { await action(); }
  catch (Exception e) when (e is InvalidDataException or EndOfStreamException or JsonException)
  { Console.WriteLine($"ok - {description}"); return; }
  throw new Exception(description);
}

var stream = new MemoryStream();
var wire = new Wire(stream);
await wire.Write(new { v = 1, id = "unicode", title = "日本語 — Word" }, default);
await wire.Write(new { v = 1, id = "second" }, default);
stream.Position = 0;
using (var first = await wire.Read(default))
  Check(first!.RootElement.GetProperty("title").GetString() == "日本語 — Word", "Unicode frame roundtrip");
using (var second = await wire.Read(default))
  Check(second!.RootElement.GetProperty("id").GetString() == "second", "Consecutive frames stay distinct");
Check(await wire.Read(default) == null, "Clean EOF");
using (var fragmented = new FragmentedStream(stream.ToArray()))
{
  using var message = await new Wire(fragmented).Read(default);
  Check(message!.RootElement.GetProperty("id").GetString() == "unicode", "Read handles byte-by-byte transport fragmentation");
}
using (var gated = new GatedStream(stream.ToArray()))
{
  await using var inbox = new RequestInbox(new Wire(gated), default);
  Check(!await inbox.AvailableWithin(TimeSpan.FromMilliseconds(10)), "Desktop polling leaves a partial frame pending");
  Check(!await inbox.AvailableWithin(TimeSpan.FromMilliseconds(10)), "Repeated desktop polling preserves the same read");
  gated.Release();
  using var message = await inbox.Read();
  Check(message!.RootElement.GetProperty("id").GetString() == "unicode", "Desktop relay receives the original complete frame after sign-in");
  using var next = await inbox.Read();
  Check(next!.RootElement.GetProperty("id").GetString() == "second", "Frame after sign-in remains aligned");
}
using (var gated = new GatedStream(stream.ToArray()))
{
  await using var inbox = new RequestInbox(new Wire(gated), default);
  await inbox.AvailableWithin(TimeSpan.FromMilliseconds(10));
  inbox.Cancel();
  try { await inbox.Read(); throw new Exception("Pending read ignored service stop"); }
  catch (OperationCanceledException) { Console.WriteLine("ok - Service stop cancels a pending partial frame"); }
}
var concurrent = new MemoryStream();
var concurrentWire = new Wire(concurrent);
await Task.WhenAll(Enumerable.Range(0, 100).Select(i => concurrentWire.Write(new { id = i }, default)));
concurrent.Position = 0;
var ids = new HashSet<int>();
while (await concurrentWire.Read(default) is { } message)
  using (message) { ids.Add(message.RootElement.GetProperty("id").GetInt32()); }
Check(ids.Count == 100, "Concurrent event and reply writes preserve frame boundaries");
var worstCase = Enumerable.Range(0, 128).Select(i => new WindowInfo(i.ToString("x"), uint.MaxValue,
  new string('\uffff', 1023), new string('\uffff', 255), int.MinValue, int.MinValue,
  int.MaxValue, int.MaxValue, true, true, true, uint.MaxValue, "ffffffffffffffff", i)).ToArray();
var bounded = new MemoryStream();
await new Wire(bounded).Write(new { v = 1, type = "windows.snapshot", sequence = long.MaxValue,
  session = int.MaxValue, capacityReached = true, windows = worstCase }, default);
Check(bounded.Length <= Wire.MaxFrame + 4, "Maximum snapshot fits even with worst-case JSON escaping");
await Reject(async () => { await new Wire(new MemoryStream()).Write(new { title = new string('x', Wire.MaxFrame) }, default); },
  "Oversized outbound frame is rejected");
foreach (uint size in new uint[] { 0, Wire.MaxFrame + 1, uint.MaxValue })
{
  var header = new byte[4];
  BinaryPrimitives.WriteUInt32LittleEndian(header, size);
  await Reject(async () => { await new Wire(new MemoryStream(header)).Read(default); }, "Reject invalid frame size");
}
await Reject(async () => { await new Wire(new MemoryStream(new byte[] { 10, 0 })).Read(default); }, "Reject truncated header");
await Reject(async () => { await new Wire(new MemoryStream(new byte[] { 10, 0, 0, 0, 123 })).Read(default); }, "Reject truncated body");
ApplicationEntry[] apps = [new("word", "Microsoft Word", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE")];
foreach (var session in new[] { "old-session", "current-session" })
{
  using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { session }));
  Check(Requests.MatchesSession(request.RootElement, "current-session") == (session == "current-session"),
    "Commands are bound to the current desktop agent session");
}
using (var request = JsonDocument.Parse("{}"))
  Check(!Requests.MatchesSession(request.RootElement, "current-session"), "Missing session is rejected");
using (var request = JsonDocument.Parse("""{"v":1,"id":"1","method":"app.launch","appId":"word"}"""))
{
  Check(Requests.Parse(request.RootElement).Method == "app.launch", "Parse versioned request");
  Check(Requests.ResolveLaunch(request.RootElement, apps) == apps[0], "Launch resolves catalog entry");
}
foreach (var json in new[] {
  """{"v":2,"id":"1","method":"app.launch"}""",
  """{"v":1,"id":"","method":"app.launch"}""" })
  await Reject(() => { using var d = JsonDocument.Parse(json); Requests.Parse(d.RootElement); return Task.CompletedTask; }, "Reject invalid request envelope");
foreach (var json in new[] {
  """{"v":1,"id":"1","method":"app.launch","appId":"cmd"}""",
  """{"v":1,"id":"1","method":"app.launch","appId":"word","arguments":"/unsafe"}""",
  """{"v":1,"id":"1","method":"app.launch","appId":"word","executable":"cmd.exe"}""" })
  await Reject(() => { using var d = JsonDocument.Parse(json); Requests.ResolveLaunch(d.RootElement, apps); return Task.CompletedTask; }, "Reject arbitrary command launch");

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
  public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
}

sealed class GatedStream(byte[] bytes) : MemoryStream(bytes)
{
  readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
  public void Release() => ready.TrySetResult();
  public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
  {
    if (Position != 0) await ready.Task.WaitAsync(cancellationToken);
    return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
  }
}

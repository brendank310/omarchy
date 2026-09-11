using System.Text.Json;

namespace Omarchy.Enhanced;

// Polling desktop availability must not cancel a half-read transport frame.
// This single-consumer inbox retains one read across polls and ownership moves
// to the desktop relay when a user signs in.
public sealed class RequestInbox(Wire wire, CancellationToken cancel) : IAsyncDisposable
{
  readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel);
  Task<JsonDocument?>? pending;

  Task<JsonDocument?> Pending => pending ??= wire.Read(lifetime.Token);

  public void Cancel() => lifetime.Cancel();

  public async Task<bool> AvailableWithin(TimeSpan interval)
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    var read = Pending;
    await Task.WhenAny(read, Task.Delay(interval, timeout.Token));
    timeout.Cancel();
    lifetime.Token.ThrowIfCancellationRequested();
    return read.IsCompleted;
  }

  public async Task<JsonDocument?> Read()
  {
    var read = Pending;
    try { return await read; }
    finally { pending = null; }
  }

  public async ValueTask DisposeAsync()
  {
    lifetime.Cancel();
    if (pending != null)
    {
      try { (await pending)?.Dispose(); }
      catch (Exception e) when (e is OperationCanceledException or IOException or JsonException or ObjectDisposedException) { }
    }
    lifetime.Dispose();
  }
}

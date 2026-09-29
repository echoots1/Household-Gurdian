using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Guardian.Contracts;

namespace Guardian.Tray;

/// <summary>
/// Client for the service's named pipe \\.\pipe\GuardianTray. Byte mode, newline-framed JSON
/// (camelCase, so <see cref="PipeRequest.Type"/> goes out as "t"). One request line → one response line.
/// Reconnects with 1 s → 10 s backoff. Not thread-safe by itself; callers serialise through <see cref="Send"/>.
/// </summary>
internal sealed class PipeClient : IDisposable
{
    // JsonStringEnumConverter reads TrayState as either "Warning" or 1, whichever the service writes.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private TimeSpan _backoff = MinBackoff;
    private DateTime _nextConnectAttempt = DateTime.MinValue;
    private bool _wasConnected;

    /// <summary>True after the last exchange succeeded; false while the service is unreachable.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Sends one request and returns the response, or null when the service is unreachable or the
    /// exchange failed. Blocks for at most a few seconds; call it off the UI thread.
    /// </summary>
    public PipeResponse? Send(PipeRequest request)
    {
        lock (_gate)
        {
            if (!EnsureConnected()) return null;
            try
            {
                var line = JsonSerializer.Serialize(request, Json) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);

                using var cts = new CancellationTokenSource(IoTimeout);
                // Pipes do not support ReadTimeout, so the timeout is enforced by cancelling the async calls.
                _pipe!.WriteAsync(bytes, cts.Token).AsTask().GetAwaiter().GetResult();
                _pipe.FlushAsync(cts.Token).GetAwaiter().GetResult();
                var reply = _reader!.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
                if (reply is null) throw new IOException("pipe closed by server");

                var response = JsonSerializer.Deserialize<PipeResponse>(reply, Json);
                MarkConnected();
                return response;
            }
            catch (Exception ex)
            {
                Log.Error("Pipe exchange failed", ex);
                Drop();
                return null;
            }
        }
    }

    private bool EnsureConnected()
    {
        if (_pipe is { IsConnected: true }) return true;
        Drop();
        if (DateTime.UtcNow < _nextConnectAttempt) return false;

        try
        {
            var pipe = new NamedPipeClientStream(".", Names.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect((int)IoTimeout.TotalMilliseconds);
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            MarkConnected();
            return true;
        }
        catch (Exception ex)
        {
            // Only log the first failure in a run of failures, so an absent service does not fill the log.
            if (_wasConnected || _nextConnectAttempt == DateTime.MinValue)
                Log.Error("Pipe connect failed", ex);
            _wasConnected = false;
            _nextConnectAttempt = DateTime.UtcNow + _backoff;
            _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
            Drop();
            return false;
        }
    }

    private void MarkConnected()
    {
        if (!_wasConnected) Log.Info("Pipe connected");
        _wasConnected = true;
        IsConnected = true;
        _backoff = MinBackoff;
        _nextConnectAttempt = DateTime.MinValue;
    }

    private void Drop()
    {
        if (IsConnected) Log.Info("Pipe disconnected");
        IsConnected = false;
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _pipe = null;
    }

    public void Dispose()
    {
        lock (_gate) Drop();
    }
}

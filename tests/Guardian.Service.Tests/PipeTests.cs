using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Guardian.Contracts;
using Xunit;

namespace Guardian.Service.Tests;

/// <summary>Talks to the real named pipe the hosted PipeServerWorker opens, the way the tray does.</summary>
public class PipeTests : IDisposable
{
    private readonly GuardianFactory _f = new();
    public void Dispose() => _f.Dispose();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    [Fact]
    public async Task Round_trip_over_the_named_pipe()
    {
        _ = _f.Services; // boot the host and its workers
        using var pipe = new NamedPipeClientStream(".", Names.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        var reader = new StreamReader(pipe, new UTF8Encoding(false));
        async Task<PipeResponse> Send(PipeRequest r)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r, Json) + "\n");
            await pipe.WriteAsync(bytes); await pipe.FlushAsync();
            var line = await reader.ReadLineAsync();
            return JsonSerializer.Deserialize<PipeResponse>(line!, Json)!;
        }
        var resp = await Send(new PipeRequest { Type = "status", User = "kid", SessionId = 1 });
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Status);
        // Setup isn't done in this test, so no account is monitored: the tray is simply "on".
        Assert.Equal(TrayState.On, resp.Status!.State);
        var resp2 = await Send(new PipeRequest { Type = "sample", User = "kid", SessionId = 1, Sample = new Sample { At = DateTimeOffset.Now, User = "kid", SessionId = 1, Process = "steam" } });
        Assert.True(resp2.Ok);
        Assert.Contains("setup not finished", resp2.Status!.Tooltip);
        Assert.True(resp2.Status.NoticeAccepted); // never show the notice screen to an account that is not monitored
    }
}

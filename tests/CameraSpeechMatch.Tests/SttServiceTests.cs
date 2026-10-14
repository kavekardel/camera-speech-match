using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CameraSpeechMatch;
using CameraSpeechMatchSample.Configuration;
using CameraSpeechMatchSample.Services.Stt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatch.Tests;

public sealed class SttServiceTests
{
    [Theory]
    [InlineData("fa")]
    [InlineData("en")]
    public async Task Transcribe_SendsOnlyPcmAfterReadyAndWaitsForCompletion(string language)
    {
        byte[] audio = Enumerable.Range(0, 3_000).Select(i => (byte)i).ToArray();
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            var configuration = await ReceiveAsync(socket, token);
            Assert.Equal(WebSocketMessageType.Text, configuration.Type);
            using var document = JsonDocument.Parse(configuration.Bytes);
            var root = document.RootElement;
            Assert.Equal(language, root.GetProperty("language").GetString());
            Assert.Equal("pcm_s16le", root.GetProperty("audioFormat").GetString());
            Assert.Equal(16_000, root.GetProperty("sampleRate").GetInt32());
            Assert.Equal(1, root.GetProperty("channels").GetInt32());

            await SendAsync(socket, "{\"type\":", token, endOfMessage: false);
            await SendAsync(socket, "\"ready\"}", token);
            Assert.Equal(audio, await ReceiveAudioAsync(socket, token));
            await SendAsync(socket, """{"type":"transcript","text":"ignored interim","isFinal":false}""", token);
            await SendAsync(socket, """{"type":"transcript","text":"Hello","isFinal":true}""", token);
            await SendAsync(socket, """{"type":"transcript","text":"world","isFinal":true}""", token);
            await SendAsync(socket, """{"type":"transcript","text":"!","isFinal":true}""", token);
            await SendAsync(socket, """{"type":"completed"}""", token);
        });

        Assert.Equal("Hello world!", await server.Service().TranscribeAsync(Request(audio, language), default));
    }

    [Theory]
    [InlineData("{not-json", typeof(InvalidDataException))]
    [InlineData("""{"type":"transcript","text":1}""", typeof(InvalidDataException))]
    [InlineData("""{"type":"transcript","text":"x","isFinal":"true"}""", typeof(InvalidDataException))]
    [InlineData("""{"type":"error","code":"capacity_reached"}""", typeof(HttpRequestException))]
    [InlineData("""{"type":"completed","type":"error"}""", typeof(InvalidDataException))]
    [InlineData("[]", typeof(InvalidDataException))]
    public async Task Transcribe_RejectsInvalidOrErrorEvents(string response, Type expected)
    {
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            await SendAsync(socket, """{"type":"ready"}""", token);
            await ReceiveAudioAsync(socket, token);
            await SendAsync(socket, response, token);
        });

        Assert.IsType(expected, await Record.ExceptionAsync(() => server.Service().TranscribeAsync(Request(), default)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10 * 1024 * 1024 + 2)]
    public async Task Transcribe_RejectsInvalidAudioBeforeConnecting(int bytes)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().TranscribeAsync(Request(new byte[bytes]), default));
    }

    [Fact]
    public async Task Transcribe_MissingPcmDoesNotConnect()
    {
        var request = new SttTranscribeRequest { Language = "en" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().TranscribeAsync(request, default));
    }

    [Fact]
    public async Task Transcribe_UnsupportedLanguageDoesNotConnect()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().TranscribeAsync(Request(language: "fr"), default));
    }

    [Fact]
    public async Task Transcribe_BoundsFragmentedEvents()
    {
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            await SendAsync(socket, """{"type":"ready"}""", token);
            await ReceiveAudioAsync(socket, token);
            await SendAsync(socket, new string('x', 40), token, endOfMessage: false);
            await SendAsync(socket, new string('x', 40), token);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => server.Service(eventLimit: 64).TranscribeAsync(Request(), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transcribe_RejectsPrematureClose(bool abrupt)
    {
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            await SendAsync(socket, """{"type":"ready"}""", token);
            await ReceiveAudioAsync(socket, token);
            if (abrupt) socket.Abort();
            else await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", token);
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => server.Service().TranscribeAsync(Request(), default));
    }

    [Fact]
    public async Task Transcribe_CompletionBeforeAudioIsRejected()
    {
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            await SendAsync(socket, """{"type":"ready"}""", token);
            await SendAsync(socket, """{"type":"completed"}""", token);
            await ReceiveAudioAsync(socket, token);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => server.Service().TranscribeAsync(Request(new byte[64_000]), default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transcribe_ReadinessAndFinalizationTimeoutsCloseConnection(bool waitingForReady)
    {
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            if (!waitingForReady)
            {
                await SendAsync(socket, """{"type":"ready"}""", token);
                await ReceiveAudioAsync(socket, token);
            }
            await ReceiveAsync(socket, token);
        });
        var settings = new SpeechInputSettings { ReadyTimeoutSeconds = 1, FinalizationTimeoutSeconds = 1 };

        await Assert.ThrowsAsync<TimeoutException>(() => server.Service(settings).TranscribeAsync(Request(), default));
    }

    [Fact]
    public async Task Transcribe_CallerCancellationIsPreserved()
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            connected.SetResult();
            await ReceiveAsync(socket, token);
        });
        using var cancellation = new CancellationTokenSource();
        Task<string> pending = server.Service().TranscribeAsync(Request(), cancellation.Token);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Transcribe_OverallDeadlineIncludesAudioDelivery()
    {
        bool completeReceived = false;
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            await SendAsync(socket, """{"type":"ready"}""", token);
            await ReceiveAudioAsync(socket, token);
            completeReceived = true;
        });

        await Assert.ThrowsAsync<TimeoutException>(() =>
            server.Service(new SpeechInputSettings { MaximumSessionSeconds = 1 })
                .TranscribeAsync(Request(new byte[64_000]), default));
        await server.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(completeReceived);
    }

    [Fact]
    public async Task Transcribe_DeliversBufferedAudioAtItsRecordingRate()
    {
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            await SendAsync(socket, """{"type":"ready"}""", token);
            await ReceiveAudioAsync(socket, token);
            await SendAsync(socket, """{"type":"completed"}""", token);
        });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        await server.Service().TranscribeAsync(Request(new byte[6_400]), default);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(180));
    }

    [Fact]
    public async Task Transcribe_RejectsBinaryEvents()
    {
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            await socket.SendAsync(new byte[] { 1, 2 }.AsMemory(), WebSocketMessageType.Binary, true, token);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => server.Service().TranscribeAsync(Request(), default));
    }

    [Fact]
    public async Task Transcribe_RequiresReadyBeforeSendingAudio()
    {
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            await SendAsync(socket, """{"type":"transcript","text":"early","isFinal":true}""", token);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => server.Service().TranscribeAsync(Request(), default));
    }

    [Fact]
    public async Task Transcribe_BoundsAccumulatedTranscript()
    {
        await using var server = await StartServerAsync(async (socket, token) =>
        {
            await ReceiveAsync(socket, token);
            await SendAsync(socket, """{"type":"ready"}""", token);
            await ReceiveAudioAsync(socket, token);
            string response = JsonSerializer.Serialize(new { type = "transcript", text = new string('x', 60_000), isFinal = true });
            for (int index = 0; index < 5; index++) await SendAsync(socket, response, token);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => server.Service().TranscribeAsync(Request(), default));
    }

    [Fact]
    public void AvailabilityReflectsConfiguration()
    {
        Assert.False(Service(new SpeechInputSettings { Enabled = false }).IsAvailable);
        Assert.False(Service(stt: new SttGatewaySettings { BaseUrl = "invalid" }).IsAvailable);
        Assert.True(Service().IsAvailable);
    }

    private static SttService Service(SpeechInputSettings? speech = null, SttGatewaySettings? stt = null) =>
        new(Options.Create(stt ?? new() { BaseUrl = "http://127.0.0.1:1" }),
            Options.Create(speech ?? new()), NullLogger<SttService>.Instance);

    private static SttTranscribeRequest Request(byte[]? audio = null, string language = "en") => new()
    {
        Language = language,
        PcmAudioContent = audio ?? [1, 2, 3, 4]
    };

    private static async Task<TestServer> StartServerAsync(Func<WebSocket, CancellationToken, Task> handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Map("/prefix/api/stt/v1/stream", async context =>
        {
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await handler(socket, context.RequestAborted);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { }
            catch (Exception exception) { completed.TrySetException(exception); }
            finally { completed.TrySetResult(); }
        });
        await app.StartAsync();
        return new TestServer(app, completed.Task);
    }

    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[4_096];
        var result = await socket.ReceiveAsync(buffer.AsMemory(), token);
        if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Client closed.");
        Assert.True(result.EndOfMessage);
        return (result.MessageType, buffer[..result.Count]);
    }

    private static async Task<byte[]> ReceiveAudioAsync(WebSocket socket, CancellationToken token)
    {
        using var audio = new MemoryStream();
        while (true)
        {
            var message = await ReceiveAsync(socket, token);
            if (message.Type == WebSocketMessageType.Text)
            {
                Assert.Equal("""{"type":"complete"}""", Encoding.UTF8.GetString(message.Bytes));
                return audio.ToArray();
            }
            Assert.Equal(WebSocketMessageType.Binary, message.Type);
            Assert.InRange(message.Bytes.Length, 1, 1_280);
            audio.Write(message.Bytes);
        }
    }

    private static ValueTask SendAsync(WebSocket socket, string text, CancellationToken token, bool endOfMessage = true) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, endOfMessage, token);

    private sealed class TestServer(WebApplication app, Task completed) : IAsyncDisposable
    {
        public Task Completed => completed;

        public SttService Service(SpeechInputSettings? speech = null, int eventLimit = 65_536) =>
            SttServiceTests.Service(speech, new SttGatewaySettings
            {
                BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/prefix",
                MaximumEventBytes = eventLimit
            });

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await completed.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

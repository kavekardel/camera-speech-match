using System.Net;
using System.Text;
using System.Text.Json;
using CameraSpeechMatch;
using CameraSpeechMatchSample.Configuration;
using CameraSpeechMatchSample.Services.SystemOne;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatch.Tests;

public sealed class SystemOneServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.85)]
    [InlineData(1)]
    public async Task Match_MapsIntraBooleanWireContractAndReturnsProbability(double probability)
    {
        using var handler = new Handler(async (message, _) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/prefix/api/system-one/v1/boolean", message.RequestUri!.AbsolutePath);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await message.Content.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal("Spoken transcript", root.GetProperty("state").GetString());
            Assert.Equal("Expected prompt", root.GetProperty("instructions").GetProperty("expectedText").GetString());
            Assert.Equal("Compare meaning", root.GetProperty("instructions").GetProperty("task").GetString());
            Assert.Equal("Preserve facts", root.GetProperty("instructions").GetProperty("rules")[0].GetString());
            Assert.Equal("Equivalent", root.GetProperty("criteria").GetProperty("true").GetString());
            Assert.Equal("Different", root.GetProperty("criteria").GetProperty("false").GetString());
            return Response(JsonSerializer.Serialize(new { probability, usage = new { inputTokens = 10, outputTokens = 5 } }));
        });
        using var client = Client(handler);
        Assert.Equal(probability, await Service(client).NoulTypeAsync(Request(), default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("invalid-json")]
    [InlineData("{\"probability\":0.8}")]
    [InlineData("{\"usage\":{\"inputTokens\":1,\"outputTokens\":2}}")]
    [InlineData("{\"probability\":1.1,\"usage\":{\"inputTokens\":1,\"outputTokens\":2}}")]
    [InlineData("{\"probability\":-0.1,\"usage\":{\"inputTokens\":1,\"outputTokens\":2}}")]
    [InlineData("{\"probability\":\"0.8\",\"usage\":{\"inputTokens\":1,\"outputTokens\":2}}")]
    [InlineData("{\"probability\":1e999,\"usage\":{\"inputTokens\":1,\"outputTokens\":2}}")]
    [InlineData("{\"probability\":0.8,\"usage\":null}")]
    [InlineData("{\"probability\":0.8,\"usage\":{\"inputTokens\":-1,\"outputTokens\":2}}")]
    [InlineData("{\"probability\":0.8,\"usage\":{\"inputTokens\":1}}")]
    [InlineData("{\"probability\":0.8,\"usage\":{\"inputTokens\":1,\"outputTokens\":2,\"costUsd\":-1}}")]
    [InlineData("{\"probability\":0.8,\"probability\":0.9,\"usage\":{\"inputTokens\":1,\"outputTokens\":2}}")]
    public async Task Match_RejectsMalformedMissingOrInvalidResponseFields(string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(json)));
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).NoulTypeAsync(Request(), default));
    }

    [Theory]
    [InlineData(400, typeof(HttpRequestException))]
    [InlineData(503, typeof(HttpRequestException))]
    [InlineData(202, typeof(InvalidDataException))]
    [InlineData(204, typeof(InvalidDataException))]
    public async Task Match_RequiresHttp200(int status, Type exceptionType)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var client = Client(handler);
        Exception? exception = await Record.ExceptionAsync(() => Service(client).NoulTypeAsync(Request(), default));
        Assert.IsType(exceptionType, exception);
        if (exception is HttpRequestException http) Assert.Equal((HttpStatusCode)status, http.StatusCode);
    }

    [Fact]
    public async Task Match_EnforcesRequestAndStreamingResponseLimits()
    {
        int calls = 0;
        using var handler = new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableStream(new byte[128]))
            });
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(client, new() { MaxRequestBytes = 1 }).NoulTypeAsync(Request(), default));
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client, new() { MaxResponseBytes = 32 }).NoulTypeAsync(Request(), default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Match_TimeoutIncludesResponseBodyReading()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream([], delay: true))
        }));
        using var client = Client(handler);
        await Assert.ThrowsAsync<TimeoutException>(() => Service(client, new() { TimeoutSeconds = 1 }).NoulTypeAsync(Request(), default));
    }

    [Fact]
    public async Task Match_LargeConfiguredResponseLimitDoesNotOverflow()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(
            "{\"probability\":0.5,\"usage\":{\"inputTokens\":1,\"outputTokens\":2}}")));
        using var client = Client(handler);
        Assert.Equal(0.5, await Service(client, new() { MaxResponseBytes = int.MaxValue }).NoulTypeAsync(Request(), default));
    }

    [Fact]
    public async Task Match_PreservesCallerCancellation()
    {
        using var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response("{}");
        });
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource();
        Task<double> pending = Service(client).NoulTypeAsync(Request(), cancellation.Token);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Match_UnavailableConfigurationFailsBeforeNetworkCall()
    {
        using var handler = new Handler((_, _) => throw new Exception("Should not call"));
        using var client = Client(handler);
        var service = Service(client, new() { Enabled = false });
        Assert.False(service.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.NoulTypeAsync(Request(), default));
        client.BaseAddress = null;
        Assert.False(Service(client).IsAvailable);
    }

    private static SystemOneService Service(HttpClient client, SystemOneSettings? settings = null) =>
        new(client, Options.Create(settings ?? new()), NullLogger<SystemOneService>.Instance);
    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://gateway.test/prefix/"), Timeout = Timeout.InfiniteTimeSpan
    };
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    private static SystemOneMatchProbabilityRequest Request() => new()
    {
        ExpectedText = "Expected prompt", Transcript = "Spoken transcript", Task = "Compare meaning",
        Rules = ["Preserve facts"], TrueCriterion = "Equivalent", FalseCriterion = "Different"
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes, bool delay = false) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delay) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

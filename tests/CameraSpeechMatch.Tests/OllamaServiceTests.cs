using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using CameraSpeechMatch;
using CameraSpeechMatchSample.Configuration;
using CameraSpeechMatchSample.Services.Ollama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatch.Tests;

public sealed class OllamaServiceTests
{
    private const string SttReply = """{"done":true,"done_reason":"stop","message":{"content":"language English<asr_text>Hello world."}}""";

    [Fact]
    public async Task Stt_SendsActualWavAudioWithEmptyPromptAndParsesTranscript()
    {
        byte[] pcm = [1, 2, 3, 4];
        using var setup = await Setup.CreateAsync(async (request, token) =>
        {
            Assert.Equal("/prefix/api/chat", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            Assert.Equal("frozenlab/qwen3-asr:0.6b", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            var message = root.GetProperty("messages")[0];
            Assert.Equal("user", message.GetProperty("role").GetString());
            Assert.Equal("", message.GetProperty("content").GetString());
            byte[] wav = message.GetProperty("images")[0].GetBytesFromBase64();
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
            Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
            Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
            Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
            Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
            Assert.Equal(pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
            Assert.Equal(pcm, wav[44..]);
            return Reply(SttReply);
        });
        Assert.Equal("Hello world.", await setup.Stt.TranscribeAsync(new() { PcmAudioContent = pcm }, default));
    }

    [Fact]
    public async Task Stt_ChunksLongRecordingsWithoutLosingSamples()
    {
        int[] lengths = [960_000, 960_000, 2];
        int calls = 0;
        using var setup = await Setup.CreateAsync(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var wav = json.RootElement.GetProperty("messages")[0].GetProperty("images")[0].GetBytesFromBase64();
            Assert.Equal(lengths[calls++], wav.Length - 44);
            return Reply(SttReply);
        });
        Assert.Equal("Hello world. Hello world. Hello world.",
            await setup.Stt.TranscribeAsync(new() { PcmAudioContent = new byte[lengths.Sum()] }, default));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10 * 1024 * 1024 + 2)]
    public async Task Stt_RejectsInvalidAudioBeforeHttp(int size)
    {
        using var setup = await Setup.CreateAsync((_, _) => throw new Exception("Unexpected inference"));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Stt.TranscribeAsync(new() { PcmAudioContent = new byte[size] }, default));
    }

    [Fact]
    public async Task Stt_RejectsMissingAudioAndContainerFormats()
    {
        using var setup = await Setup.CreateAsync((_, _) => throw new Exception("Unexpected inference"));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Stt.TranscribeAsync(new(), default));
        foreach (byte[] bytes in new byte[][] { "RIFF00"u8.ToArray(), "OggS00"u8.ToArray(), "ID3000"u8.ToArray(), [0x1a, 0x45, 0xdf, 0xa3] })
            await Assert.ThrowsAsync<ArgumentException>(() => setup.Stt.TranscribeAsync(new() { PcmAudioContent = bytes }, default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"done\":true,\"done_reason\":\"length\",\"message\":{\"content\":\"language English<asr_text>partial\"}}")]
    [InlineData("{\"done\":true,\"done_reason\":\"stop\",\"message\":{\"content\":\"hallucinated text without marker\"}}")]
    [InlineData("{\"done\":true,\"done_reason\":\"stop\",\"message\":{\"content\":\"language English<asr_text> \"}}")]
    [InlineData("{\"done\":true,\"done_reason\":\"stop\",\"message\":{\"content\":\"<asr_text>one<asr_text>two\"}}")]
    [InlineData("{\"done\":true,\"done\":false}")]
    [InlineData("{\"error\":\"runner failed\"}")]
    public async Task Stt_RejectsMalformedEmptyOrIncompleteResponses(string json)
    {
        using var setup = await Setup.CreateAsync((_, _) => Task.FromResult(Reply(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.Stt.TranscribeAsync(Audio(), default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(0.2)]
    [InlineData(0.8)]
    [InlineData(0.85)]
    [InlineData(1)]
    public async Task Matching_SendsStructuredJevQuestionAndParsesProbability(double probability)
    {
        using var setup = await Setup.CreateAsync(async (request, token) =>
        {
            Assert.Equal("/prefix/v1/systemone", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            Assert.Equal("nimble:9b-q4_K_M", root.GetProperty("model").GetString());
            Assert.Equal("Expected phrase", root.GetProperty("state").GetProperty("expectedText").GetString());
            Assert.Equal("Spoken phrase", root.GetProperty("state").GetProperty("transcript").GetString());
            var question = root.GetProperty("questions").GetProperty("match");
            Assert.Equal("noul", question.GetProperty("type").GetString());
            Assert.Equal("Compare meaning. Preserve facts.", question.GetProperty("instructions").GetString());
            Assert.Equal("Equivalent", question.GetProperty("criteria").GetProperty("true").GetString());
            Assert.Equal("Different", question.GetProperty("criteria").GetProperty("false").GetString());
            return Reply(JsonSerializer.Serialize(new { answers = new { match = new { type = "noul", noul = probability } } }));
        });
        Assert.Equal(probability, await setup.Matcher.MatchProbabilityAsync(Match(), default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("invalid")]
    [InlineData("{\"answers\":{\"match\":{\"type\":\"choice\",\"noul\":0.9}}}")]
    [InlineData("{\"answers\":{\"match\":{\"type\":\"noul\",\"noul\":\"0.9\"}}}")]
    [InlineData("{\"answers\":{\"match\":{\"type\":\"noul\",\"noul\":1.1}}}")]
    [InlineData("{\"answers\":{\"match\":{\"type\":\"noul\",\"noul\":-0.1}}}")]
    [InlineData("{\"answers\":{\"match\":{\"type\":\"noul\",\"noul\":1e999}}}")]
    [InlineData("{\"answers\":{\"match\":{\"type\":\"noul\",\"noul\":0.5}}}")]
    [InlineData("{\"answers\":{\"match\":{\"type\":\"noul\",\"noul\":0.79}}}")]
    [InlineData("{\"answers\":{\"match\":{\"type\":\"noul\",\"noul\":0.9,\"noul\":0.1}}}")]
    public async Task Matching_FailsClosedForInvalidOrUncertainDecisions(string json)
    {
        using var setup = await Setup.CreateAsync((_, _) => Task.FromResult(Reply(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.Matcher.MatchProbabilityAsync(Match(), default));
    }

    [Fact]
    public async Task Matching_RejectsBlankStateAndOversizedRequest()
    {
        using var setup = await Setup.CreateAsync((_, _) => throw new Exception("Unexpected inference"), new() { MaximumMatchingRequestBytes = 1024 });
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Matcher.MatchProbabilityAsync(new()
        {
            State = " ", Instructions = "Compare meaning"
        }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Matcher.MatchProbabilityAsync(Match(new string('x', 2048)), default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Matching_PreservesTextObjectAndArrayContentWithoutAddingGuidance(int contentKind)
    {
        object state = contentKind switch
        {
            0 => "Customer asks for a refund.",
            1 => new { ticket = "Customer asks for a refund." },
            _ => new[] { "Customer asks for a refund." }
        };
        object instructions = contentKind switch
        {
            0 => "Is a refund requested?",
            1 => new { question = "Is a refund requested?" },
            _ => new[] { "Is a refund requested?" }
        };
        using var setup = await Setup.CreateAsync(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            Assert.Equal(JsonSerializer.Serialize(state, OllamaClient.JsonOptions), root.GetProperty("state").GetRawText());
            var question = root.GetProperty("questions").GetProperty("match");
            Assert.Equal(JsonSerializer.Serialize(instructions, OllamaClient.JsonOptions), question.GetProperty("instructions").GetRawText());
            Assert.False(question.TryGetProperty("criteria", out _));
            return Reply("""{"answers":{"match":{"type":"noul","noul":0.9}}}""");
        });
        Assert.Equal(0.9, await setup.Matcher.MatchProbabilityAsync(new() { State = state, Instructions = instructions }, default));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Refund requested", null)]
    [InlineData(null, "No refund requested")]
    [InlineData("Refund requested", "No refund requested")]
    public async Task Matching_MapsTypedOptionalNoulCriteria(string? trueCriteria, string? falseCriteria)
    {
        using var setup = await Setup.CreateAsync(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var question = json.RootElement.GetProperty("questions").GetProperty("match");
            if (trueCriteria is null && falseCriteria is null)
            {
                Assert.False(question.TryGetProperty("criteria", out _));
            }
            else
            {
                var sent = question.GetProperty("criteria");
                Assert.Equal((trueCriteria is null ? 0 : 1) + (falseCriteria is null ? 0 : 1), sent.EnumerateObject().Count());
                Assert.Equal(trueCriteria is not null, sent.TryGetProperty("true", out var trueValue));
                Assert.Equal(falseCriteria is not null, sent.TryGetProperty("false", out var falseValue));
                if (trueCriteria is not null) Assert.Equal(trueCriteria, trueValue.GetString());
                if (falseCriteria is not null) Assert.Equal(falseCriteria, falseValue.GetString());
            }
            return Reply("""{"answers":{"match":{"type":"noul","noul":0.9}}}""");
        });
        Assert.Equal(0.9, await setup.Matcher.MatchProbabilityAsync(new()
        {
            State = "A ticket", Instructions = "Is a refund requested?",
            TrueCriteria = trueCriteria, FalseCriteria = falseCriteria
        }, default));
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("true", true)]
    [InlineData("42", true)]
    [InlineData("\" \"", true)]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("42", false)]
    [InlineData("\"\"", false)]
    public async Task Matching_RejectsInvalidStateOrInstructionsBeforeInference(string json, bool invalidState)
    {
        using var setup = await Setup.CreateAsync((_, _) => throw new Exception("Unexpected inference"));
        var invalid = JsonSerializer.Deserialize<JsonElement>(json);
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Matcher.MatchProbabilityAsync(new()
        {
            State = invalidState ? invalid : "A ticket",
            Instructions = invalidState ? "Is a refund requested?" : invalid
        }, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Services_DistinguishTimeoutAndCallerCancellation(bool stt)
    {
        using var setup = await Setup.CreateAsync(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Reply("{}");
        }, new() { TranscriptionTimeoutSeconds = 1, MatchingTimeoutSeconds = 1 });
        Task Invoke(CancellationToken token) => stt ? setup.Stt.TranscribeAsync(Audio(), token) : setup.Matcher.MatchProbabilityAsync(Match(), token);
        await Assert.ThrowsAsync<TimeoutException>(() => Invoke(default));
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke(cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Client_BoundsStreamedBodiesAndAppliesTimeoutDuringBodyRead()
    {
        using var setup = await Setup.CreateAsync((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[2048]))
        }), new() { MaximumResponseBytes = 1024 });
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.Client.SendAsync(HttpMethod.Get, "oversized", null, 1, default));
        using var delayed = await Setup.CreateAsync((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DelayedStream())
        }));
        await Assert.ThrowsAsync<TimeoutException>(() => delayed.Client.SendAsync(HttpMethod.Get, "delayed", null, 1, default));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Client_ReportsOllamaHttpErrorsWithoutReadingSensitiveBody(int status)
    {
        using var setup = await Setup.CreateAsync((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => setup.Stt.TranscribeAsync(Audio(), default));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
    }

    [Theory]
    [InlineData("version", "{\"version\":\"0.34.0\"}", "Update Ollama")]
    [InlineData("tags", "{\"models\":[{\"name\":\"frozenlab/qwen3-asr:0.6b\"}]}", "ollama pull nimble:9b-q4_K_M")]
    [InlineData("tags", "{\"models\":[]}", "ollama pull frozenlab/qwen3-asr:0.6b")]
    [InlineData("show", "{\"capabilities\":[\"completion\"]}", "audio support")]
    public async Task Readiness_ReportsMissingModelsAndUnsupportedCapabilities(string path, string json, string errorText)
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Reply(
            request.RequestUri!.AbsolutePath.EndsWith(path) ? json : Metadata(request))))) { BaseAddress = new("http://localhost/") };
        var ready = new OllamaReadiness();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ready.CheckAsync(new(http, Options.Create(new OllamaSettings())), new(), default));
        Assert.Contains(errorText, error.Message);
        Assert.False(ready.IsReady);
        Assert.Equal(HealthStatus.Unhealthy, (await ready.CheckHealthAsync(new())).Status);
    }

    [Fact]
    public async Task Readiness_RetriesTransientFailureWithinBoundedStartup()
    {
        int attempts = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("version") && ++attempts == 1) throw new HttpRequestException("not yet");
            return Task.FromResult(Reply(Metadata(request)));
        })) { BaseAddress = new("http://localhost/") };
        var options = Options.Create(new OllamaSettings { StartupTimeoutSeconds = 5, RetryDelaySeconds = 1 });
        var ready = new OllamaReadiness();
        using var service = new OllamaStartupService(new(http, options), options, ready, NullLogger<OllamaStartupService>.Instance);
        await service.StartAsync(default);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.True(ready.IsReady);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Readiness_StopsRetryingAndServicesRemainUnavailable()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline"))) { BaseAddress = new("http://localhost/") };
        var options = Options.Create(new OllamaSettings { StartupTimeoutSeconds = 1 });
        var ready = new OllamaReadiness();
        var client = new OllamaClient(http, options);
        using var service = new OllamaStartupService(client, options, ready, NullLogger<OllamaStartupService>.Instance);
        await service.StartAsync(default);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(ready.IsReady);
        Assert.Contains("timed out", ready.Status);
        Assert.Contains("offline", ready.Status);
        Assert.False(new OllamaSttService(client, options, ready).IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OllamaSystemOneService(client, options, ready).MatchProbabilityAsync(Match(), default));
    }

    [Fact]
    public void Registration_UsesLocalProvidersAndAspireDiscoveryWithoutCredentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = "http://ignored:11434", ["services:ollama:http:0"] = "http://localhost:12345/prefix/"
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddLocalOllama(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<OllamaSttService>(provider.GetRequiredService<ISttService>());
        Assert.IsType<OllamaSystemOneService>(provider.GetRequiredService<ISystemOneService>());
        Assert.Equal("http://localhost:12345/prefix/", provider.GetRequiredService<IOptions<OllamaSettings>>().Value.BaseUrl);
        Assert.Equal("nimble:9b-q4_K_M", provider.GetRequiredService<IOptions<OllamaSettings>>().Value.MatchingModel);
    }

    [Theory]
    [InlineData("Ollama:BaseUrl", "ftp://localhost")]
    [InlineData("Ollama:MatchingModel", " ")]
    [InlineData("Ollama:StartupTimeoutSeconds", "0")]
    [InlineData("Ollama:MinimumDecisionProbability", "0.4")]
    public void Registration_ValidatesConfiguration(string key, string value)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddLocalOllama(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build());
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OllamaSettings>>().Value);
    }

    internal static TranscribeRequest Audio() => new() { PcmAudioContent = [1, 2] };
    internal static async Task<OllamaReadiness> ReadyAsync()
    {
        using var setup = await Setup.CreateAsync((_, _) => throw new Exception("Unexpected inference"));
        return setup.Readiness;
    }
    internal static async Task MakeReadyAsync(OllamaReadiness readiness)
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Reply(Metadata(request)))))
        { BaseAddress = new("http://localhost/") };
        await readiness.CheckAsync(new(http, Options.Create(new OllamaSettings())), new(), default);
    }
    internal static MatchProbabilityRequest Match(string transcript = "Spoken phrase") => new()
    {
        State = new { expectedText = "Expected phrase", transcript },
        Instructions = "Compare meaning. Preserve facts.",
        TrueCriteria = "Equivalent", FalseCriteria = "Different"
    };
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static string Metadata(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.Split('/').Last() switch
    {
        "version" => """{"version":"0.40.4"}""",
        "tags" => """{"models":[{"name":"frozenlab/qwen3-asr:0.6b"},{"name":"nimble:9b-q4_K_M"}]}""",
        "show" => """{"capabilities":["completion","audio","decision"]}""",
        _ => throw new InvalidOperationException("Unexpected metadata request")
    };

    private sealed class Setup(HttpClient http, OllamaClient client, OllamaSettings settings, OllamaReadiness ready) : IDisposable
    {
        public OllamaClient Client => client;
        public OllamaReadiness Readiness => ready;
        public OllamaSttService Stt { get; } = new(client, Options.Create(settings), ready);
        public OllamaSystemOneService Matcher { get; } = new(client, Options.Create(settings), ready);
        public void Dispose() => http.Dispose();
        public static async Task<Setup> CreateAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> inference, OllamaSettings? settings = null)
        {
            settings ??= new();
            var http = new HttpClient(new Handler((request, token) =>
                request.RequestUri!.AbsolutePath.EndsWith("version") || request.RequestUri.AbsolutePath.EndsWith("tags") || request.RequestUri.AbsolutePath.EndsWith("show")
                    ? Task.FromResult(Reply(Metadata(request))) : inference(request, token)))
            { BaseAddress = new("http://localhost/prefix/"), Timeout = Timeout.InfiniteTimeSpan };
            var client = new OllamaClient(http, Options.Create(settings));
            var ready = new OllamaReadiness();
            await ready.CheckAsync(client, settings, default);
            return new(http, client, settings, ready);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class DelayedStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}

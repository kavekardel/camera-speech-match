using System.Net;
using CameraSpeechMatch.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatch.Tests;

public sealed class HostedProviderRegistrationTests
{
    [Theory]
    [InlineData("Deepgram", "Jev")]
    [InlineData("ElevenLabs", "CloudflareClef")]
    [InlineData("Deepgram", "CloudflareClef")]
    [InlineData("ElevenLabs", "Jev")]
    public async Task MixedHostedProvidersAreReadyAfterConfigurationWithoutStartupInference(string transcription, string matching)
    {
        var values = Settings(transcription, matching);
        using var provider = new ServiceCollection().AddCameraSpeechMatchAI(Configuration(values)).BuildServiceProvider();
        await AiRegistrationTests.StartAsync(provider);
        Assert.True(provider.GetRequiredService<ISttService>().IsAvailable);
        Assert.True(provider.GetRequiredService<ISystemOneService>().IsAvailable);
        Assert.True(provider.GetRequiredService<CameraSpeechMatchAiReadiness>().IsReadyFor(CameraSpeechMatchMode.Combined));
    }

    [Theory]
    [InlineData("Deepgram", "Transcription:ApiKey", "")]
    [InlineData("ElevenLabs", "Transcription:ApiKey", " ")]
    [InlineData("Deepgram", "Transcription:Model", "")]
    [InlineData("ElevenLabs", "Transcription:Model", "")]
    [InlineData("Deepgram", "Transcription:Language", " ")]
    [InlineData("ElevenLabs", "Transcription:Language", "fa\nvalue")]
    [InlineData("Jev", "SemanticMatching:ApiKey", "")]
    [InlineData("Jev", "SemanticMatching:Model", "")]
    [InlineData("Jev", "SemanticMatching:MinimumDecisionProbability", "NaN")]
    [InlineData("CloudflareClef", "SemanticMatching:ApiKey", "")]
    [InlineData("CloudflareClef", "SemanticMatching:AccountId", "")]
    [InlineData("CloudflareClef", "SemanticMatching:AccountId", "../other")]
    [InlineData("CloudflareClef", "SemanticMatching:Model", "unknown-model")]
    [InlineData("CloudflareClef", "SemanticMatching:MinimumDecisionProbability", "0.4")]
    public async Task EnabledProviderCredentialsModelsAndSettingsAreValidatedAtStartup(string name, string key, string value)
    {
        var values = Settings(
            name is "Deepgram" or "ElevenLabs" ? name : "None",
            name is "Jev" or "CloudflareClef" ? name : "None");
        values[key] = value;
        using var provider = new ServiceCollection().AddCameraSpeechMatchAI(Configuration(values)).BuildServiceProvider();
        await Assert.ThrowsAsync<OptionsValidationException>(() => AiRegistrationTests.StartAsync(provider));
    }

    [Fact]
    public async Task ExplicitLanguageIsRejectedForAutomaticOnlyOllama()
    {
        var values = Settings("Ollama", "None");
        values["Transcription:Language"] = "fa";
        using var provider = new ServiceCollection().AddCameraSpeechMatchAI(Configuration(values)).BuildServiceProvider();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => AiRegistrationTests.StartAsync(provider));
        Assert.Contains("automatic language detection", error.Message);
    }

    [Theory]
    [InlineData("Jev", "api.typesafe.ai", "/v1/systemone")]
    [InlineData("CloudflareClef", "api.cloudflare.com", "/client/v4/accounts/account123/ai/run/@cf/cloudflare/clef-flash")]
    [InlineData("Deepgram", "api.deepgram.com", "/v1/listen")]
    [InlineData("ElevenLabs", "api.elevenlabs.io", "/v1/speech-to-text")]
    public async Task ProviderDefaultsUseTheOfficialApiBases(string name, string host, string path)
    {
        bool transcription = name is "Deepgram" or "ElevenLabs";
        var services = new ServiceCollection().AddCameraSpeechMatchAI(Configuration(Settings(
            transcription ? name : "None", transcription ? "None" : name)));
        Uri? actual = null;
        services.AddHttpClient(transcription
            ? CameraSpeechMatchAiServiceCollectionExtensions.TranscriptionHttpClientName
            : CameraSpeechMatchAiServiceCollectionExtensions.SemanticMatchingHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new Handler((request, _) =>
            {
                actual = request.RequestUri;
                string body = name switch
                {
                    "Jev" => """{"answers":{"match":{"type":"noul","noul":0.9}}}""",
                    "CloudflareClef" => """{"success":true,"errors":[],"result":{"answers":{"match":{"type":"noul","noul":0.9}}}}""",
                    "Deepgram" => """{"results":{"channels":[{"alternatives":[{"transcript":"Hello"}]}]}}""",
                    _ => """{"text":"Hello"}"""
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }));
        using var provider = services.BuildServiceProvider();
        await AiRegistrationTests.StartAsync(provider);
        if (transcription) await provider.GetRequiredService<ISttService>().TranscribeAsync(new() { PcmAudioContent = [1, 2] }, default);
        else await provider.GetRequiredService<ISystemOneService>().MatchProbabilityAsync(OllamaServiceTests.Match(), default);
        Assert.Equal("https", actual!.Scheme);
        Assert.Equal(host, actual.Host);
        Assert.Equal(path, actual.AbsolutePath);
    }

    [Fact]
    public async Task ExistingNumericProviderConfigurationStillResolvesOpenAI()
    {
        var values = Settings("2", "None");
        values["Transcription:Endpoint"] = "http://localhost/v1";
        using var provider = new ServiceCollection().AddCameraSpeechMatchAI(Configuration(values)).BuildServiceProvider();
        await AiRegistrationTests.StartAsync(provider);
        Assert.IsType<OpenAISttService>(provider.GetRequiredService<ISttService>());
    }

    private static Dictionary<string, string?> Settings(string transcription, string matching) => new()
    {
        ["Transcription:Provider"] = transcription,
        ["Transcription:Model"] = transcription == "Deepgram" ? "nova-3" : "scribe_v2",
        ["Transcription:ApiKey"] = "test-key",
        ["SemanticMatching:Provider"] = matching,
        ["SemanticMatching:Model"] = matching == "Jev" ? "jev-1.14.0" : "clef-flash",
        ["SemanticMatching:ApiKey"] = "test-key",
        ["SemanticMatching:AccountId"] = "account123"
    };

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            action(request, cancellationToken);
    }
}

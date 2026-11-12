using System.Net;
using System.Text.Json;
using CameraSpeechMatch.AI;
using CameraSpeechMatch.AI.Ollama;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatch.Tests;

public sealed class AiRegistrationTests
{
    [Fact]
    public async Task DefaultsSupplyUnavailableServicesAndLexicalNeedsOnlyTranscription()
    {
        using var provider = new ServiceCollection().AddCameraSpeechMatchAI().BuildServiceProvider();
        Assert.False(provider.GetRequiredService<ISttService>().IsAvailable);
        Assert.False(provider.GetRequiredService<ISystemOneService>().IsAvailable);
        await StartAsync(provider);
        Assert.False(provider.GetRequiredService<CameraSpeechMatchAiReadiness>().IsReadyFor(CameraSpeechMatchMode.Lexical));
    }

    [Fact]
    public async Task DisabledProvidersIgnoreUnusedSettings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transcription:Provider"] = "None", ["Transcription:TimeoutSeconds"] = "not-a-number",
            ["SemanticMatching:Provider"] = "None", ["SemanticMatching:MinimumDecisionProbability"] = "invalid"
        }).Build();
        using var provider = new ServiceCollection().AddCameraSpeechMatchAI(configuration).BuildServiceProvider();
        await StartAsync(provider);
        Assert.False(provider.GetRequiredService<ISttService>().IsAvailable);
        Assert.False(provider.GetRequiredService<ISystemOneService>().IsAvailable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverridesBeforeOrAfterRegistrationBypassUnusedProviderValidationAndProbes(bool before)
    {
        var services = new ServiceCollection();
        void Overrides()
        {
            services.AddScoped<ISttService, CustomStt>();
            services.AddScoped<ISystemOneService, CustomMatcher>();
        }
        if (before) Overrides();
        services.AddCameraSpeechMatchAI(options =>
        {
            options.Transcription.Provider = TranscriptionProvider.Ollama;
            options.Transcription.Endpoint = "invalid";
            options.SemanticMatching.Provider = SemanticMatchingProvider.OpenAICompatible;
            options.SemanticMatching.TimeoutSeconds = 0;
        });
        if (!before) Overrides();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await StartAsync(provider);
        using var scope = provider.CreateScope();
        Assert.IsType<CustomStt>(scope.ServiceProvider.GetRequiredService<ISttService>());
        Assert.IsType<CustomMatcher>(scope.ServiceProvider.GetRequiredService<ISystemOneService>());
        Assert.True(provider.GetRequiredService<CameraSpeechMatchAiReadiness>().IsReadyFor(CameraSpeechMatchMode.Combined));
    }

    [Fact]
    public async Task OverriddenOperationDoesNotBindMalformedUnusedConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transcription:Provider"] = "AnUnknownProvider",
            ["Transcription:TimeoutSeconds"] = "not-a-number",
            ["SemanticMatching:Provider"] = "OpenAICompatible",
            ["SemanticMatching:Endpoint"] = "http://localhost/v1",
            ["SemanticMatching:Model"] = "chat"
        }).Build();
        var services = new ServiceCollection().AddCameraSpeechMatchAI(configuration);
        services.AddSingleton<ISttService, CustomStt>();
        using var provider = services.BuildServiceProvider();
        await StartAsync(provider);
        Assert.True(provider.GetRequiredService<ISystemOneService>().IsAvailable);
    }

    [Fact]
    public async Task HostedProviderOverridesBypassUnusedCredentialsAndMalformedSettings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transcription:Provider"] = "Deepgram",
            ["Transcription:TimeoutSeconds"] = "not-a-number",
            ["SemanticMatching:Provider"] = "CloudflareClef",
            ["SemanticMatching:Model"] = "unknown-model"
        }).Build();
        var services = new ServiceCollection().AddCameraSpeechMatchAI(configuration);
        services.AddScoped<ISttService, CustomStt>();
        services.AddScoped<ISystemOneService, CustomMatcher>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await StartAsync(provider);
        Assert.True(provider.GetRequiredService<CameraSpeechMatchAiReadiness>().IsReadyFor(CameraSpeechMatchMode.Combined));
    }

    [Fact]
    public async Task ConfigurationOverloadBindsEachOperationAndDoesNotRegisterHostChatClient()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Transcription:Provider"] = "OpenAICompatible",
            ["AI:Transcription:Endpoint"] = "http://localhost:54321/prefix/v1",
            ["AI:Transcription:Model"] = "speech",
            ["AI:SemanticMatching:Provider"] = "None"
        }).Build();
        var services = new ServiceCollection().AddCameraSpeechMatchAI(configuration.GetSection("AI"));
        using var provider = services.BuildServiceProvider();
        await StartAsync(provider); // Does not contact the cloud endpoint.
        Assert.IsType<OpenAISttService>(provider.GetRequiredService<ISttService>());
        Assert.Null(provider.GetService<IChatClient>());
        var readiness = provider.GetRequiredService<CameraSpeechMatchAiReadiness>();
        Assert.True(readiness.IsReadyFor(CameraSpeechMatchMode.Lexical));
        Assert.False(readiness.IsReadyFor(CameraSpeechMatchMode.Semantic));
        Assert.False(readiness.SemanticMatching.IsConfigured);
    }

    [Fact]
    public async Task ExistingHostChatClientIsPreserved()
    {
        var unrelated = new OpenAI.Chat.ChatClient("unrelated-model", "unused").AsIChatClient();
        var services = new ServiceCollection().AddSingleton(unrelated).AddCameraSpeechMatchAI(options =>
        {
            options.SemanticMatching.Provider = SemanticMatchingProvider.OpenAICompatible;
            options.SemanticMatching.Endpoint = "http://localhost/v1";
            options.SemanticMatching.Model = "component-model";
        });
        using var provider = services.BuildServiceProvider();
        await StartAsync(provider);
        Assert.Same(unrelated, provider.GetRequiredService<IChatClient>());
        Assert.IsType<ChatSemanticMatchService>(provider.GetRequiredService<ISystemOneService>());
    }

    [Theory]
    [InlineData("Transcription:Endpoint", "ftp://localhost")]
    [InlineData("Transcription:Endpoint", "http://secret@localhost")]
    [InlineData("Transcription:Endpoint", "http://localhost?key=secret")]
    [InlineData("Transcription:Model", " ")]
    [InlineData("Transcription:TimeoutSeconds", "0")]
    [InlineData("Transcription:MaximumAudioBytes", "10485761")]
    [InlineData("Transcription:MaximumResponseBytes", "1")]
    [InlineData("Transcription:ApiKey", "secret\nvalue")]
    [InlineData("SemanticMatching:MinimumDecisionProbability", "0.4")]
    [InlineData("SemanticMatching:MinimumDecisionProbability", "NaN")]
    public async Task EnabledProviderSettingsAreValidatedAtHostStartup(string key, string value)
    {
        var values = new Dictionary<string, string?>
        {
            ["Transcription:Provider"] = "Ollama", ["Transcription:Model"] = "speech",
            ["SemanticMatching:Provider"] = "OllamaSystemOne", ["SemanticMatching:Model"] = "decision",
            [key] = value
        };
        using var provider = new ServiceCollection().AddCameraSpeechMatchAI(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(provider));
    }

    [Fact]
    public async Task OfficialOpenAIRequiresCredentialsButSelfHostedEndpointsDoNot()
    {
        using var provider = new ServiceCollection().AddCameraSpeechMatchAI(options =>
        {
            options.Transcription.Provider = TranscriptionProvider.OpenAICompatible;
            options.Transcription.Model = "speech";
        }).BuildServiceProvider();
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(provider));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedProvidersProbeOnlyTheirOwnOllamaModel(bool localTranscription)
    {
        var services = new ServiceCollection().AddCameraSpeechMatchAI(options =>
        {
            options.Transcription.Provider = localTranscription ? TranscriptionProvider.Ollama : TranscriptionProvider.OpenAICompatible;
            options.Transcription.Endpoint = "http://localhost/prefix/";
            options.Transcription.Model = "speech";
            options.SemanticMatching.Provider = localTranscription ? SemanticMatchingProvider.OpenAICompatible : SemanticMatchingProvider.OllamaSystemOne;
            options.SemanticMatching.Endpoint = "http://localhost/prefix/";
            options.SemanticMatching.Model = "decision";
        });
        int probes = 0;
        services.AddHttpClient(localTranscription
            ? CameraSpeechMatchAiServiceCollectionExtensions.TranscriptionHttpClientName
            : CameraSpeechMatchAiServiceCollectionExtensions.SemanticMatchingHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(async (request, token) =>
            {
                Interlocked.Increment(ref probes);
                string model = localTranscription ? "speech" : "decision";
                string capability = localTranscription ? "audio" : "decision";
                string body = request.RequestUri!.AbsolutePath.Split('/').Last() switch
                {
                    "version" => """{"version":"0.40.4"}""",
                    "tags" => JsonSerializer.Serialize(new { models = new[] { new { name = model } } }),
                    "show" => JsonSerializer.Serialize(new { capabilities = new[] { capability } }),
                    _ => throw new Exception("Unexpected inference")
                };
                if (request.Content is not null)
                    Assert.Contains(model, await request.Content.ReadAsStringAsync(token));
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }));
        using var provider = services.BuildServiceProvider();
        var readiness = provider.GetRequiredService<CameraSpeechMatchAiReadiness>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        readiness.Changed += () => { if (readiness.IsReadyFor(CameraSpeechMatchMode.Combined)) ready.TrySetResult(); };
        await StartAsync(provider);
        if (!readiness.IsReadyFor(CameraSpeechMatchMode.Combined))
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, probes);
        Assert.True(provider.GetRequiredService<ISttService>().IsAvailable);
        Assert.True(provider.GetRequiredService<ISystemOneService>().IsAvailable);
        foreach (var service in provider.GetServices<IHostedService>()) await service.StopAsync(default);
    }

    internal static async Task StartAsync(IServiceProvider provider)
    {
        foreach (var service in provider.GetServices<IHostedService>()) await service.StartAsync(default);
    }

    private sealed class CustomStt : ISttService
    {
        public bool IsAvailable => true;
        public Task<string> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken) => Task.FromResult("text");
    }
    private sealed class CustomMatcher : ISystemOneService
    {
        public bool IsAvailable => true;
        public Task<double> MatchProbabilityAsync(MatchProbabilityRequest request, CancellationToken cancellationToken) => Task.FromResult(1d);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
}

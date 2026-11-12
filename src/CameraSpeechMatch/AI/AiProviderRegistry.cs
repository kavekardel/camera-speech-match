using System.Collections.Concurrent;
using CameraSpeechMatch.AI.Ollama;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatch.AI;

internal sealed class AiProviderRegistry(
    Func<CameraSpeechMatchTranscriptionOptions> transcriptionOptions, Func<CameraSpeechMatchSemanticMatchingOptions> matchingOptions, IHttpClientFactory httpFactory,
    CameraSpeechMatchAiReadiness readiness, ILoggerFactory loggerFactory) : IDisposable
{
    private readonly ConcurrentBag<IDisposable> resources = [];
    private readonly ConcurrentBag<OllamaStartupService> startupServices = [];
    internal ISttService? Transcription { get; private set; }
    internal ISystemOneService? SemanticMatching { get; private set; }

    internal ISttService CreateTranscription()
    {
        var settings = transcriptionOptions();
        if (!Enum.IsDefined(settings.Provider)) throw Invalid("Transcription.Provider is invalid.");
        Transcription = settings.Provider switch
        {
            TranscriptionProvider.None => new UnavailableSttService(),
            TranscriptionProvider.Ollama => CreateOllamaTranscription(settings),
            TranscriptionProvider.OpenAICompatible => CreateCloudTranscription(settings),
            TranscriptionProvider.Deepgram or TranscriptionProvider.ElevenLabs => CreateRestTranscription(settings),
            _ => throw Invalid("Transcription.Provider is invalid.")
        };
        return Transcription;
    }

    internal ISystemOneService CreateSemanticMatching()
    {
        var settings = matchingOptions();
        if (!Enum.IsDefined(settings.Provider)) throw Invalid("SemanticMatching.Provider is invalid.");
        SemanticMatching = settings.Provider switch
        {
            SemanticMatchingProvider.None => new UnavailableSystemOneService(),
            SemanticMatchingProvider.OllamaSystemOne => CreateOllamaMatching(settings),
            SemanticMatchingProvider.OpenAICompatible => CreateCloudMatching(settings),
            SemanticMatchingProvider.Jev or SemanticMatchingProvider.CloudflareClef => CreateHostedSystemOne(settings),
            _ => throw Invalid("SemanticMatching.Provider is invalid.")
        };
        return SemanticMatching;
    }

    private ISttService CreateCloudTranscription(CameraSpeechMatchTranscriptionOptions settings)
    {
        Uri endpoint = Validate(settings, "https://api.openai.com/v1");
        ValidateAudio(settings);
        var service = new OpenAISttService(CreateHttp(true, endpoint, settings), endpoint, settings);
        readiness.Set(true, new(true, true, "Speech provider is configured; inference has not been probed."));
        return service;
    }

    private ISystemOneService CreateCloudMatching(CameraSpeechMatchSemanticMatchingOptions settings)
    {
        Uri endpoint = Validate(settings, "https://api.openai.com/v1");
        ValidateMatching(settings, false);
        var service = new ChatSemanticMatchService(CreateHttp(false, endpoint, settings), endpoint, settings);
        readiness.Set(false, new(true, true, "Semantic provider is configured; inference has not been probed."));
        return service;
    }

    private ISttService CreateRestTranscription(CameraSpeechMatchTranscriptionOptions settings)
    {
        string endpointDefault = settings.Provider == TranscriptionProvider.Deepgram
            ? "https://api.deepgram.com" : "https://api.elevenlabs.io";
        Uri endpoint = Validate(settings, endpointDefault, requireApiKey: true);
        ValidateAudio(settings);
        var client = new AiHttpClient(CreateHttp(true, endpoint, settings), settings.MaximumResponseBytes, settings.Provider.ToString());
        ISttService service = settings.Provider switch
        {
            TranscriptionProvider.Deepgram => new DeepgramSttService(client, settings),
            TranscriptionProvider.ElevenLabs => new ElevenLabsSttService(client, settings),
            _ => throw Invalid("The REST transcription provider is invalid.")
        };
        readiness.Set(true, new(true, true, "Speech provider is configured; inference has not been probed."));
        return service;
    }

    private ISystemOneService CreateHostedSystemOne(CameraSpeechMatchSemanticMatchingOptions settings)
    {
        string endpointDefault = settings.Provider == SemanticMatchingProvider.Jev
            ? "https://api.typesafe.ai" : "https://api.cloudflare.com/client/v4";
        Uri endpoint = Validate(settings, endpointDefault, requireApiKey: true);
        ValidateMatching(settings, true);
        if (settings.Provider == SemanticMatchingProvider.CloudflareClef)
        {
            if (string.IsNullOrWhiteSpace(settings.AccountId) || settings.AccountId.Any(character => !char.IsAsciiLetterOrDigit(character)))
                throw Invalid("CloudflareClef requires an alphanumeric AccountId.");
            if (settings.Model is not ("clef" or "clef-flash"))
                throw Invalid("CloudflareClef Model must be clef or clef-flash.");
        }
        var client = new AiHttpClient(CreateHttp(false, endpoint, settings), settings.MaximumResponseBytes, settings.Provider.ToString());
        ISystemOneService service = settings.Provider switch
        {
            SemanticMatchingProvider.Jev => new JevSystemOneService(client, settings),
            SemanticMatchingProvider.CloudflareClef => new CloudflareSystemOneService(client, settings),
            _ => throw Invalid("The hosted System One provider is invalid.")
        };
        readiness.Set(false, new(true, true, "Semantic provider is configured; inference has not been probed."));
        return service;
    }

    private ISttService CreateOllamaTranscription(CameraSpeechMatchTranscriptionOptions settings)
    {
        Uri endpoint = Validate(settings, "http://localhost:11434", probeOllama: true);
        ValidateAudio(settings);
        var native = NativeSettings(settings, true);
        native.MaximumAudioBytes = settings.MaximumAudioBytes;
        var (client, ready) = CreateOllama(true, endpoint, settings, native);
        return new OllamaSttService(client, Options.Create(native), ready);
    }

    private ISystemOneService CreateOllamaMatching(CameraSpeechMatchSemanticMatchingOptions settings)
    {
        Uri endpoint = Validate(settings, "http://localhost:11434", probeOllama: true);
        ValidateMatching(settings, true);
        var native = NativeSettings(settings, false);
        native.MaximumMatchingRequestBytes = settings.MaximumMatchingRequestBytes;
        native.MinimumDecisionProbability = settings.MinimumDecisionProbability;
        var (client, ready) = CreateOllama(false, endpoint, settings, native);
        return new OllamaSystemOneService(client, Options.Create(native), ready);
    }

    private (OllamaClient, OllamaReadiness) CreateOllama(bool transcription, Uri endpoint, CameraSpeechMatchAiOperationOptions settings, OllamaSettings native)
    {
        var http = CreateHttp(transcription, endpoint, settings);
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            http.DefaultRequestHeaders.Authorization = new("Bearer", settings.ApiKey);
        var client = new OllamaClient(http, Options.Create(native));
        var ready = new OllamaReadiness();
        void Refresh() => readiness.Set(transcription, new(true, ready.IsReady, ready.Status));
        ready.Changed += Refresh;
        Refresh();
        startupServices.Add(new(client, Options.Create(native), ready, loggerFactory.CreateLogger<OllamaStartupService>()));
        return (client, ready);
    }

    private HttpClient CreateHttp(bool transcription, Uri endpoint, CameraSpeechMatchAiOperationOptions settings)
    {
        // Own only these HttpClient instances; IHttpClientFactory owns their pooled handlers.
        var http = httpFactory.CreateClient(transcription
            ? CameraSpeechMatchAiServiceCollectionExtensions.TranscriptionHttpClientName
            : CameraSpeechMatchAiServiceCollectionExtensions.SemanticMatchingHttpClientName);
        http.BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
        resources.Add(http);
        return http;
    }

    private static OllamaSettings NativeSettings(CameraSpeechMatchAiOperationOptions settings, bool transcription) => new()
    {
        SttModel = transcription ? settings.Model! : "",
        MatchingModel = transcription ? "" : settings.Model!,
        TranscriptionTimeoutSeconds = settings.TimeoutSeconds,
        MatchingTimeoutSeconds = settings.TimeoutSeconds,
        StartupTimeoutSeconds = settings.StartupTimeoutSeconds,
        ProbeTimeoutSeconds = settings.ProbeTimeoutSeconds,
        RetryDelaySeconds = settings.RetryDelaySeconds,
        MaximumResponseBytes = settings.MaximumResponseBytes
    };

    internal async Task StartAsync(CancellationToken token)
    {
        foreach (var service in startupServices) await service.StartAsync(token);
    }

    internal async Task StopAsync(CancellationToken token)
    {
        foreach (var service in startupServices) await service.StopAsync(token);
    }

    private static Uri Validate(CameraSpeechMatchAiOperationOptions settings, string defaultEndpoint,
        bool probeOllama = false, bool requireApiKey = false)
    {
        string endpoint = settings.Endpoint ?? defaultEndpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw Invalid("An enabled AI Endpoint must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        if (settings.ApiKey?.Any(char.IsControl) == true)
            throw Invalid("ApiKey must not contain control characters.");
        if (string.IsNullOrWhiteSpace(settings.Model)) throw Invalid("An enabled AI operation requires an explicit Model.");
        if ((requireApiKey || uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)) && string.IsNullOrWhiteSpace(settings.ApiKey))
            throw Invalid("The configured AI provider requires an ApiKey.");
        if (settings.TimeoutSeconds is < 1 or > 600 || settings.MaximumResponseBytes is < 1024 or > 1_048_576 ||
            probeOllama && (settings.StartupTimeoutSeconds is < 1 or > 300 || settings.ProbeTimeoutSeconds is < 1 or > 30 ||
                settings.RetryDelaySeconds is < 1 or > 30))
            throw Invalid("AI timeouts or response byte limits are outside their supported range.");
        return uri;
    }

    private static void ValidateAudio(CameraSpeechMatchTranscriptionOptions settings)
    {
        if (settings.MaximumAudioBytes is < 2 or > 10 * 1024 * 1024)
            throw Invalid("MaximumAudioBytes must be between 2 and 10485760.");
        if (settings.Language is not null &&
            (string.IsNullOrWhiteSpace(settings.Language) || settings.Language.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))))
            throw Invalid("Language must be a nonblank provider-supported language code.");
        if (settings.Provider == TranscriptionProvider.Ollama && settings.Language is not null)
            throw Invalid("Ollama ASR uses automatic language detection. Omit Language for this provider.");
    }

    private static void ValidateMatching(CameraSpeechMatchSemanticMatchingOptions settings, bool native)
    {
        if (settings.MaximumMatchingRequestBytes is < 1024 or > 65_536 ||
            native && (!double.IsFinite(settings.MinimumDecisionProbability) || settings.MinimumDecisionProbability is < 0.5 or > 1))
            throw Invalid("Semantic request limits or System One certainty settings are invalid.");
    }

    private static OptionsValidationException Invalid(string message) => new(
        Options.DefaultName, typeof(CameraSpeechMatchAiOptions), [message]);

    public void Dispose()
    {
        foreach (var service in startupServices) service.Dispose();
        foreach (var resource in resources) resource.Dispose();
    }
}

internal sealed class UnavailableSttService : ISttService
{
    public bool IsAvailable => false;
    public Task<string> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken) =>
        Task.FromException<string>(new InvalidOperationException("Speech recognition is not configured."));
}

internal sealed class UnavailableSystemOneService : ISystemOneService
{
    public bool IsAvailable => false;
    public Task<double> MatchProbabilityAsync(MatchProbabilityRequest request, CancellationToken cancellationToken) =>
        Task.FromException<double>(new InvalidOperationException("Semantic matching is not configured."));
}

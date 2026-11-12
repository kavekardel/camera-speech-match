using CameraSpeechMatch.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatch;

public static class CameraSpeechMatchAiServiceCollectionExtensions
{
    /// <summary>Named HTTP clients can be configured without changing unrelated host AI clients.</summary>
    public const string TranscriptionHttpClientName = "CameraSpeechMatch.Transcription";
    public const string SemanticMatchingHttpClientName = "CameraSpeechMatch.SemanticMatching";

    /// <summary>Reads each operation independently from the supplied AI section. Use the action overload to compose host-specific overrides.</summary>
    public static IServiceCollection AddCameraSpeechMatchAI(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<CameraSpeechMatchAiOptions>().Bind(configuration);
        // Bind operations independently so an overridden operation cannot break the other one.
        return AddCore(services,
            _ => ReadTranscription(configuration.GetSection(nameof(CameraSpeechMatchAiOptions.Transcription))),
            _ => ReadMatching(configuration.GetSection(nameof(CameraSpeechMatchAiOptions.SemanticMatching))));
    }

    public static IServiceCollection AddCameraSpeechMatchAI(this IServiceCollection services, Action<CameraSpeechMatchAiOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        return AddCore(services);
    }

    public static IServiceCollection AddCameraSpeechMatchAI(this IServiceCollection services) => AddCore(services);

    private static CameraSpeechMatchTranscriptionOptions ReadTranscription(IConfiguration section) =>
        section.GetValue<TranscriptionProvider>("Provider") == TranscriptionProvider.None
            ? new() : section.Get<CameraSpeechMatchTranscriptionOptions>() ?? new();

    private static CameraSpeechMatchSemanticMatchingOptions ReadMatching(IConfiguration section) =>
        section.GetValue<SemanticMatchingProvider>("Provider") == SemanticMatchingProvider.None
            ? new() : section.Get<CameraSpeechMatchSemanticMatchingOptions>() ?? new();

    private static IServiceCollection AddCore(IServiceCollection services,
        Func<IServiceProvider, CameraSpeechMatchTranscriptionOptions>? transcription = null,
        Func<IServiceProvider, CameraSpeechMatchSemanticMatchingOptions>? matching = null)
    {
        services.AddOptions<CameraSpeechMatchAiOptions>();
        services.AddLogging();
        services.AddHttpClient(TranscriptionHttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient(SemanticMatchingHttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan);
        services.TryAddSingleton<CameraSpeechMatchAiReadiness>();
        services.TryAddSingleton(provider => new AiProviderRegistry(
            () => transcription is null
                ? provider.GetRequiredService<IOptions<CameraSpeechMatchAiOptions>>().Value.Transcription
                : transcription(provider),
            () => matching is null
                ? provider.GetRequiredService<IOptions<CameraSpeechMatchAiOptions>>().Value.SemanticMatching
                : matching(provider),
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<CameraSpeechMatchAiReadiness>(),
            provider.GetRequiredService<ILoggerFactory>()));
        services.TryAddSingleton<ISttService>(provider => provider.GetRequiredService<AiProviderRegistry>().CreateTranscription());
        services.TryAddSingleton<ISystemOneService>(provider => provider.GetRequiredService<AiProviderRegistry>().CreateSemanticMatching());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AiStartupService>());
        services.AddHealthChecks().AddCheck<CameraSpeechMatchAiReadiness>("camera-speech-match-ai", tags: ["ready"]);
        return services;
    }
}

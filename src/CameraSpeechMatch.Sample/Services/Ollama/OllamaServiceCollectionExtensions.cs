using CameraSpeechMatch;
using CameraSpeechMatchSample.Configuration;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatchSample.Services.Ollama;

public static class OllamaServiceCollectionExtensions
{
    public static IServiceCollection AddLocalOllama(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OllamaSettings>().Bind(configuration.GetSection(OllamaSettings.SectionName))
            .PostConfigure(settings => settings.BaseUrl = configuration["services:ollama:http:0"] ??
                configuration["services:ollama:https:0"] ?? settings.BaseUrl)
            .ValidateDataAnnotations()
            .Validate(settings => Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment),
                "Ollama:BaseUrl must be an absolute HTTP(S) URL without a query or fragment.")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.SttModel) && !string.IsNullOrWhiteSpace(settings.MatchingModel),
                "Both Ollama model names are required.")
            .ValidateOnStart();
        services.AddHttpClient<OllamaClient>((provider, http) =>
        {
            http.BaseAddress = new Uri(provider.GetRequiredService<IOptions<OllamaSettings>>().Value.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<OllamaReadiness>();
        services.AddTransient<ISttService, OllamaSttService>();
        services.AddTransient<ISystemOneService, OllamaSystemOneService>();
        services.AddHostedService<OllamaStartupService>();
        services.AddHealthChecks().AddCheck<OllamaReadiness>("ollama", tags: ["ready"]);
        return services;
    }
}

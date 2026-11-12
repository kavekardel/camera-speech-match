using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CameraSpeechMatch.AI;

internal sealed class AiStartupService(
    IServiceScopeFactory scopeFactory, AiProviderRegistry registry, CameraSpeechMatchAiReadiness readiness) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolve the effective registrations first. Overridden operations never create/validate a provider.
        using var scope = scopeFactory.CreateScope();
        var stt = scope.ServiceProvider.GetRequiredService<ISttService>();
        var matcher = scope.ServiceProvider.GetRequiredService<ISystemOneService>();
        if (!ReferenceEquals(stt, registry.Transcription))
            readiness.Set(true, new(true, stt.IsAvailable, stt.IsAvailable ? "Host speech service is available." : "Host speech service is unavailable."));
        if (!ReferenceEquals(matcher, registry.SemanticMatching))
            readiness.Set(false, new(true, matcher.IsAvailable, matcher.IsAvailable ? "Host semantic service is available." : "Host semantic service is unavailable."));
        await registry.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => registry.StopAsync(cancellationToken);
}

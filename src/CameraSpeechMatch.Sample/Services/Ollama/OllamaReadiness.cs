using System.Text.Json;
using CameraSpeechMatchSample.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CameraSpeechMatchSample.Services.Ollama;

public sealed class OllamaReadiness : IHealthCheck
{
    private string? failure = "Waiting for local Ollama and the configured models.";
    public bool IsReady => Volatile.Read(ref failure) is null;
    public string Status => Volatile.Read(ref failure) ?? "Local AI models are ready.";
    public event Action? Changed;

    public async Task CheckAsync(OllamaClient client, OllamaSettings settings, CancellationToken cancellationToken)
    {
        using var version = await client.SendAsync(HttpMethod.Get, "api/version", null, settings.ProbeTimeoutSeconds, cancellationToken);
        if (!version.RootElement.TryGetProperty("version", out var value) || value.ValueKind != JsonValueKind.String ||
            !Version.TryParse(value.GetString()?.Split('-')[0], out var installed) || installed < new Version(0, 35, 0))
            throw new InvalidOperationException("Ollama 0.35 or newer with qwen3a audio and /v1/systemone support is required. Update Ollama.");
        using var tags = await client.SendAsync(HttpMethod.Get, "api/tags", null, settings.ProbeTimeoutSeconds, cancellationToken);
        if (!tags.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Ollama returned an invalid model list.");
        var names = models.EnumerateArray().Select(model =>
            model.ValueKind == JsonValueKind.Object && model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() : null).ToHashSet(StringComparer.Ordinal);
        var missing = new[] { settings.SttModel, settings.MatchingModel }.Where(name => !names.Contains(name)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("Missing Ollama models. Install explicitly: " +
                string.Join("; ", missing.Select(name => $"ollama pull {name}")));
        await CheckCapabilityAsync(settings.SttModel, "audio");
        await CheckCapabilityAsync(settings.MatchingModel, "decision");
        SetStatus(null);

        async Task CheckCapabilityAsync(string model, string capability)
        {
            using var show = await client.SendAsync(HttpMethod.Post, "api/show", new { model }, settings.ProbeTimeoutSeconds, cancellationToken);
            if (!show.RootElement.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array ||
                !capabilities.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == capability))
                throw new InvalidOperationException($"Ollama model {model} does not advertise {capability} support. Check the model and update Ollama; no substitute is selected.");
        }
    }

    public void SetFailure(string message) => SetStatus(message);
    private void SetStatus(string? message)
    {
        if (Interlocked.Exchange(ref failure, message) != message) Changed?.Invoke();
    }
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsReady ? HealthCheckResult.Healthy(Status) : HealthCheckResult.Unhealthy(Status));
}

public sealed class OllamaStartupService(
    OllamaClient client, IOptions<OllamaSettings> options, OllamaReadiness readiness,
    ILogger<OllamaStartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.StartupTimeoutSeconds));
        string lastError = readiness.Status;
        try
        {
            while (true)
            {
                try
                {
                    await readiness.CheckAsync(client, options.Value, deadline.Token);
                    logger.LogInformation("Local Ollama is ready; both configured models advertise the required capabilities.");
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException or TimeoutException or InvalidDataException or InvalidOperationException)
                {
                    lastError = exception.Message;
                    readiness.SetFailure(lastError);
                }
                await Task.Delay(TimeSpan.FromSeconds(options.Value.RetryDelaySeconds), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            readiness.SetFailure($"Local AI readiness timed out. {lastError} Restart the Sample after fixing Ollama.");
            logger.LogError("{ReadinessError}", readiness.Status);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

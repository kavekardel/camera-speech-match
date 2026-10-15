using System.Net;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CameraSpeechMatch.AppHost.Tests;

public sealed class AppHostTests
{
    [AspireFact]
    [Trait("Category", "AspireIntegration")]
    public async Task AppHost_StartsSampleAndSuppliesExternalOllamaEndpoint()
    {
        // This local HTTP double validates orchestration/discovery independently of model inference.
        var serverBuilder = WebApplication.CreateBuilder();
        serverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var ollama = serverBuilder.Build();
        int probes = 0;
        ollama.MapGet("/api/version", () => { Interlocked.Increment(ref probes); return new { version = "0.40.4" }; });
        ollama.MapGet("/api/tags", () => new { models = new[]
        {
            new { name = "frozenlab/qwen3-asr:0.6b" }, new { name = "nimble:9b-q4_K_M" }
        } });
        ollama.MapPost("/api/show", () => new { capabilities = new[] { "audio", "decision" } });
        await ollama.StartAsync();
        string url = ollama.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CameraSpeechMatch_AppHost>(
            ["--Ollama:BaseUrl=" + url], deadline.Token);
        await using var app = await builder.BuildAsync(deadline.Token);
        await app.StartAsync(deadline.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("sample", deadline.Token);
        using var http = app.CreateHttpClient("sample", "http");
        // The child process must have consumed the reference to our ephemeral server, not localhost:11434.
        using var response = await http.GetAsync("/health", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Volatile.Read(ref probes) > 0);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/alive", deadline.Token)).StatusCode);
        string page = await http.GetStringAsync("/", deadline.Token);
        Assert.Contains("Camera speech match", page);
    }
}

public sealed class AspireFactAttribute : FactAttribute
{
    public AspireFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ASPIRE_INTEGRATION") != "1")
            Skip = "Set ASPIRE_INTEGRATION=1 to launch the AppHost and test host-process connectivity (no Docker/models).";
    }
}

using CameraSpeechMatch;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MudBlazor.Services;
using CameraSpeechMatchSample.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddCameraSpeechMatchAI(options =>
{
    builder.Configuration.GetSection(CameraSpeechMatchAiOptions.SectionName).Bind(options);
    // Aspire discovery is host configuration; the library does not assume a service name.
    var endpoint = builder.Configuration["services:ollama:http:0"] ?? builder.Configuration["services:ollama:https:0"];
    if (endpoint is null) return;
    if (options.Transcription.Provider == TranscriptionProvider.Ollama) options.Transcription.Endpoint = endpoint;
    if (options.SemanticMatching.Provider == SemanticMatchingProvider.OllamaSystemOne) options.SemanticMatching.Endpoint = endpoint;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) => await context.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString(),
        details = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Description)
    })
});
app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = _ => false });
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

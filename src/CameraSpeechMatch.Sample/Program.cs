using CameraSpeechMatch;
using CameraSpeechMatchSample.Configuration;
using CameraSpeechMatchSample.Services.Stt;
using CameraSpeechMatchSample.Services.SystemOne;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using CameraSpeechMatchSample.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.Configure<SttGatewaySettings>(builder.Configuration.GetSection("SttGateway"));
builder.Services.Configure<SpeechInputSettings>(builder.Configuration.GetSection("SpeechInput"));
builder.Services.Configure<SystemOneSettings>(builder.Configuration.GetSection("SystemOne"));
builder.Services.AddSingleton<ISttService, SttService>();
builder.Services.AddHttpClient<ISystemOneService, SystemOneService>((services, client) =>
{
    string baseUrl = services.GetRequiredService<IOptions<SttGatewaySettings>>().Value.BaseUrl;
    if (Uri.TryCreate($"{baseUrl?.Trim().TrimEnd('/')}/", UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https")
    {
        client.BaseAddress = uri;
    }
    client.Timeout = Timeout.InfiniteTimeSpan;
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

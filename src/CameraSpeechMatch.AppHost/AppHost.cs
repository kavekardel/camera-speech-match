var builder = DistributedApplication.CreateBuilder(args);

// Ollama and its installed model store stay on the host. No container or model pull is needed.
var ollama = builder.AddExternalService("ollama", builder.Configuration["Ollama:BaseUrl"]
    ?? throw new InvalidOperationException("Configure Ollama:BaseUrl in the AppHost."))
    .WithHttpHealthCheck("/api/version");

builder.AddProject<Projects.CameraSpeechMatch_Sample>("sample", launchProfileName: "http")
    .WithEndpoint("http", endpoint => endpoint.Port = null)
    .WithReference(ollama)
    .WithHttpHealthCheck("/health");

// The Sample owns bounded model readiness; an unavailable external service must not leave
// the project waiting indefinitely in Aspire's dependency queue.
builder.Build().Run();

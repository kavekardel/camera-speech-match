namespace CameraSpeechMatchSample.Configuration;

public sealed class SttGatewaySettings
{
    public string BaseUrl { get; set; } = "https://ai-stt-gateway.airparsiana.com";
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int MaximumEventBytes { get; set; } = 65_536;
}

public sealed class SpeechInputSettings
{
    public bool Enabled { get; set; } = true;
    public int ReadyTimeoutSeconds { get; set; } = 15;
    public int FinalizationTimeoutSeconds { get; set; } = 6;
    public int MaximumSessionSeconds { get; set; } = 300;
    public int MaximumAudioBytes { get; set; } = 10 * 1024 * 1024;
}

public sealed class SystemOneSettings
{
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 35;
    public int MaxRequestBytes { get; set; } = 65_536;
    public int MaxResponseBytes { get; set; } = 262_144;
}

using Bunit;
using CameraSpeechMatch;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Component = global::CameraSpeechMatch.CameraSpeechMatch;

namespace CameraSpeechMatch.Tests;

public sealed class ComponentPcmTests : BunitContext, IAsyncLifetime
{
    private readonly Transcription stt = new();
    private readonly Matcher matcher = new();
    private readonly List<CameraSpeechMatchResult> results = [];

    public ComponentPcmTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/CameraSpeechMatch/js/camera-speech-match.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<bool>("startRecording", _ => true).SetResult(true);
        Services.AddMudServices();
        Services.AddSingleton<ISttService>(stt);
        Services.AddSingleton<ISystemOneService>(matcher);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Theory]
    [InlineData(CameraSpeechMatchMode.Lexical, "Hello world", 0, "The wording matches")]
    [InlineData(CameraSpeechMatchMode.Lexical, "Different words", 0, "The wording does not match")]
    [InlineData(CameraSpeechMatchMode.Semantic, "A paraphrase", 0.8, "The meaning matches")]
    [InlineData(CameraSpeechMatchMode.Semantic, "Different meaning", 0.79, "The meaning does not match")]
    public async Task Complete_PcmProviderResultDrivesMatchingAndPlayback(CameraSpeechMatchMode mode, string transcript, double probability, string matchMessage)
    {
        stt.Text = transcript;
        matcher.Probability = probability;
        var component = await StartAsync(mode);
        var video = new MediaStream([99, 98, 97, 96]);
        var pcm = new MediaStream([1, 2, 3, 4]);
        bool completed = await component.InvokeAsync(() => component.Instance.OnTranscriptionCompletedWithPcm(
            "Relay text must not determine the match", video, "video/webm;codecs=vp8,opus", "blob:recording", true, pcm));

        Assert.True(completed);
        var result = Assert.Single(results);
        Assert.True(result.Succeeded);
        Assert.Equal(matchMessage, component.Find(".camera-speech-match__result-summary strong").TextContent);
        Assert.InRange(result.LexicalSimilarityPercent, 0, 100);
        Assert.Equal(mode == CameraSpeechMatchMode.Lexical ? (double?)100 : null, result.MinimumLexicalSimilarityPercent);
        Assert.Equal(mode == CameraSpeechMatchMode.Semantic ? (double?)(probability * 100) : null, result.SemanticSimilarityPercent);
        Assert.Equal(mode == CameraSpeechMatchMode.Semantic ? (double?)80 : null, result.MinimumSemanticSimilarityPercent);
        Assert.Equal(transcript, result.TranscribedText);
        Assert.Equal(video.Content, result.VideoContent);
        Assert.Equal(pcm.Content, Assert.Single(stt.Requests).PcmAudioContent);
        Assert.Equal(pcm.Content, result.PcmAudioContent);
        Assert.Same(stt.Requests[0].PcmAudioContent, result.PcmAudioContent);
        Assert.Equal("application/octet-stream", result.PcmAudioContentType);
        Assert.Equal(pcm.Content.LongLength, result.PcmAudioContentLength);
        Assert.EndsWith(".pcm", result.PcmAudioFileName);
        Assert.Equal(Path.GetFileNameWithoutExtension(result.VideoFileName), Path.GetFileNameWithoutExtension(result.PcmAudioFileName));
        Assert.Equal("video/webm", result.VideoContentType);
        Assert.Equal("en", stt.Requests[0].Language);
        Assert.Equal("blob:recording", component.Find("video").GetAttribute("src"));
        Assert.NotNull(component.Find("video").GetAttribute("controls"));
        Assert.Equal(mode == CameraSpeechMatchMode.Semantic ? 1 : 0, matcher.Calls);
        Assert.True(video.Disposed && pcm.Disposed);
    }

    [Fact]
    public async Task Complete_FailedFaceValidationReadsNeitherStreamAndCallsNoProvider()
    {
        var component = await StartAsync();
        var video = new MediaStream([1, 2]);
        var pcm = new MediaStream([3, 4]);
        Assert.False(await component.InvokeAsync(() => component.Instance.OnTranscriptionCompletedWithPcm(
            "", video, "video/webm", "blob:test", false, pcm)));
        Assert.Equal(0, video.Reads);
        Assert.Equal(0, pcm.Reads);
        Assert.Empty(stt.Requests);
        Assert.Equal(0, matcher.Calls);
        Assert.False(Assert.Single(results).Succeeded);
        Assert.Null(results[0].VideoContent);
        Assert.Null(results[0].PcmAudioContent);
        Assert.Null(results[0].PcmAudioContentType);
        Assert.Null(results[0].PcmAudioFileName);
        Assert.Equal(0, results[0].PcmAudioContentLength);
        Assert.True(video.Disposed && pcm.Disposed);
    }

    [Theory]
    [InlineData(0, "The wording matches")]
    [InlineData(66.67, "The wording matches")]
    [InlineData(66.68, "The wording does not match")]
    [InlineData(100, "The wording does not match")]
    public async Task Complete_LexicalSimilarityUsesCapturedMinimum(double minimum, string matchMessage)
    {
        stt.Text = "Hello world again";
        var component = await StartAsync(minimumLexical: minimum);
        Assert.True(await component.InvokeAsync(() => component.Instance.OnTranscriptionCompletedWithPcm(
            "", new MediaStream([1, 2]), "video/webm", "blob:test", true, new MediaStream([3, 4]))));

        var result = Assert.Single(results);
        Assert.True(result.Succeeded);
        Assert.Equal(66.67, result.LexicalSimilarityPercent);
        Assert.Equal(minimum, result.MinimumLexicalSimilarityPercent);
        Assert.Null(result.SemanticSimilarityPercent);
        Assert.Null(result.MinimumSemanticSimilarityPercent);
        Assert.Equal(matchMessage, component.Find(".camera-speech-match__result-summary strong").TextContent);
        Assert.Contains($"Minimum required: {minimum:0.##}%", component.Markup);
        Assert.Equal(0, matcher.Calls);
    }

    [Theory]
    [InlineData(CameraSpeechMatchMode.Lexical, -1)]
    [InlineData(CameraSpeechMatchMode.Lexical, 101)]
    [InlineData(CameraSpeechMatchMode.Lexical, double.NaN)]
    [InlineData(CameraSpeechMatchMode.Lexical, double.PositiveInfinity)]
    [InlineData(CameraSpeechMatchMode.Semantic, -0.01)]
    [InlineData(CameraSpeechMatchMode.Semantic, 101)]
    [InlineData(CameraSpeechMatchMode.Semantic, double.NaN)]
    [InlineData(CameraSpeechMatchMode.Semantic, double.PositiveInfinity)]
    public async Task Start_RejectsInvalidMinimumForSelectedMode(CameraSpeechMatchMode mode, double minimum)
    {
        await StartAsync(mode,
            minimumLexical: mode == CameraSpeechMatchMode.Lexical ? minimum : 100,
            minimumSemantic: mode == CameraSpeechMatchMode.Semantic ? minimum : 80);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.Null(result.MinimumLexicalSimilarityPercent);
        Assert.Null(result.MinimumSemanticSimilarityPercent);
        Assert.Contains("between 0 and 100", result.ErrorMessage);
        Assert.Empty(stt.Requests);
        Assert.Equal(0, matcher.Calls);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(0, 80, 0, false)]
    [InlineData(1, 100, 100, true)]
    [InlineData(0.8, 80, 80, true)]
    [InlineData(0.79, 80, 79, false)]
    [InlineData(0.79, 79, 79, true)]
    [InlineData(0.8, 81, 80, false)]
    public async Task Complete_SemanticSimilarityUsesPercentages(double probability, double minimum, double expectedPercent, bool accepted)
    {
        matcher.Probability = probability;
        var component = await StartAsync(CameraSpeechMatchMode.Semantic, minimumSemantic: minimum);
        Assert.True(await component.InvokeAsync(() => component.Instance.OnTranscriptionCompletedWithPcm(
            "", new MediaStream([1, 2]), "video/webm", "blob:test", true, new MediaStream([3, 4]))));

        var result = Assert.Single(results);
        Assert.True(result.Succeeded);
        Assert.Equal(expectedPercent, result.SemanticSimilarityPercent);
        Assert.Equal(minimum, result.MinimumSemanticSimilarityPercent);
        Assert.Null(result.MinimumLexicalSimilarityPercent);
        Assert.Equal(accepted ? "The meaning matches" : "The meaning does not match",
            component.Find(".camera-speech-match__result-summary strong").TextContent);
        Assert.Equal($"{expectedPercent:0.##}%", component.Find(".camera-speech-match__result-metric .camera-speech-match__score").TextContent);
        Assert.Contains($"Minimum required: {minimum:0.##}%", component.Markup);
        Assert.Equal(1, matcher.Calls);
    }

    [Fact]
    public async Task Recording_WithoutRelayExplainsDeferredTranscript()
    {
        var component = await StartAsync();
        string transcript = component.Find(".camera-speech-match__transcript").TextContent;
        Assert.Contains("The transcript will appear after recording.", transcript);
        Assert.Contains("Recording audio…", transcript);
        Assert.DoesNotContain("Live transcript", transcript);
    }

    [Fact]
    public async Task Complete_EmptyTranscriptReportsFailureWithoutSemanticCall()
    {
        stt.Text = " ";
        var component = await StartAsync(CameraSpeechMatchMode.Semantic);
        Assert.False(await component.InvokeAsync(() => component.Instance.OnTranscriptionCompletedWithPcm(
            "", new MediaStream([1, 2]), "video/webm", "blob:test", true, new MediaStream([3, 4]))));
        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.Null(result.VideoContent);
        Assert.Null(result.PcmAudioContent);
        Assert.Contains("empty transcript", result.ErrorMessage);
        Assert.Equal(0, matcher.Calls);
    }

    [Fact]
    public async Task Complete_SemanticFailureClearsVideoAndPcm()
    {
        matcher.Probability = double.NaN;
        var component = await StartAsync(CameraSpeechMatchMode.Semantic);
        Assert.False(await component.InvokeAsync(() => component.Instance.OnTranscriptionCompletedWithPcm(
            "", new MediaStream([1, 2]), "video/webm", "blob:test", true, new MediaStream([3, 4]))));

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.Null(result.VideoContent);
        Assert.Null(result.PcmAudioContent);
        Assert.NotNull(Assert.Single(stt.Requests).PcmAudioContent);
        Assert.Equal("application/octet-stream", result.PcmAudioContentType);
        Assert.EndsWith(".pcm", result.PcmAudioFileName);
        Assert.Equal(2, result.PcmAudioContentLength);
        Assert.Equal(1, matcher.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10 * 1024 * 1024 + 2)]
    public async Task Complete_InvalidPcmLengthFailsBeforeReadingMedia(long length)
    {
        var component = await StartAsync();
        var video = new MediaStream([1, 2]);
        var pcm = new MediaStream([3, 4]) { Length = length };
        Assert.False(await component.InvokeAsync(() => component.Instance.OnTranscriptionCompletedWithPcm(
            "", video, "video/webm", "blob:test", true, pcm)));
        Assert.Equal(0, video.Reads + pcm.Reads);
        Assert.Empty(stt.Requests);
    }

    [Fact]
    public async Task Complete_MissingOptedInPcmFailsWithoutProviderCall()
    {
        var component = await StartAsync();
        Assert.False(await component.InvokeAsync(() => component.Instance.OnTranscriptionCompleted(
            "", new MediaStream([1, 2]), "video/webm", "blob:test", true)));
        Assert.Empty(stt.Requests);
    }

    [Fact]
    public async Task Complete_ExistingCallbackStillWorksWithCaptureDisabled()
    {
        var component = await StartAsync(capture: false);
        Assert.True(await component.InvokeAsync(() => component.Instance.OnTranscriptionCompleted(
            "", new MediaStream([1, 2]), "video/mp4", "blob:test", true)));
        Assert.Null(Assert.Single(stt.Requests).PcmAudioContent);
        Assert.Null(Assert.Single(results).PcmAudioContent);
        Assert.Null(results[0].PcmAudioContentType);
        Assert.Null(results[0].PcmAudioFileName);
        Assert.Equal(0, results[0].PcmAudioContentLength);
        Assert.True(Assert.Single(results).Succeeded);
        Assert.Contains("Verified", component.Find(".camera-speech-match__status").TextContent);
    }

    [Fact]
    public async Task Complete_IncompletePcmDoesNotCallProvider()
    {
        var component = await StartAsync();
        Assert.False(await component.InvokeAsync(() => component.Instance.OnTranscriptionCompletedWithPcm(
            "", new MediaStream([1, 2]), "video/webm", "blob:test", true,
            new MediaStream([3, 4]) { Length = 4 })));
        Assert.Empty(stt.Requests);
    }

    [Fact]
    public async Task Complete_DisposalCancelsPendingTranscriptionAndReportsNoResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stt.Handler = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return ""; };
        var component = await StartAsync();
        Task<bool> pending = component.InvokeAsync(() => component.Instance.OnTranscriptionCompletedWithPcm(
            "", new MediaStream([1, 2]), "video/webm", "blob:test", true, new MediaStream([3, 4])));
        await entered.Task;
        await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());
        Assert.False(await pending);
        Assert.Empty(results);
    }

    private async Task<IRenderedComponent<Component>> StartAsync(CameraSpeechMatchMode mode = CameraSpeechMatchMode.Lexical, bool capture = true,
        double minimumLexical = 100, double minimumSemantic = 80)
    {
        Render<MudPopoverProvider>();
        var component = Render<Component>(parameters => parameters
            .Add(p => p.ExpectedText, "Hello world")
            .Add(p => p.Language, "en-US")
            .Add(p => p.MatchMode, mode)
            .Add(p => p.MinimumLexicalSimilarityPercent, minimumLexical)
            .Add(p => p.MinimumSemanticSimilarityPercent, minimumSemantic)
            .Add(p => p.ShowResultDetails, true)
            .Add(p => p.CapturePcmAudio, capture)
            .Add(p => p.OnResult, result => results.Add(result)));
        await component.FindAll("button").First(button => button.TextContent.Contains("Start recording")).ClickAsync(new MouseEventArgs());
        await component.InvokeAsync(() => component.Instance.OnFacePositionChanged("valid", true));
        return component;
    }

    private sealed class Transcription : ISttService
    {
        public bool IsAvailable => true;
        public string Text { get; set; } = "Hello world";
        public Func<CancellationToken, Task<string>>? Handler { get; set; }
        public List<SttTranscribeRequest> Requests { get; } = [];
        public Task<string> TranscribeAsync(SttTranscribeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Handler?.Invoke(cancellationToken) ?? Task.FromResult(Text);
        }
    }
    private sealed class Matcher : ISystemOneService
    {
        public bool IsAvailable => true;
        public double Probability { get; set; } = 0.9;
        public int Calls { get; private set; }
        public Task<double> NoulTypeAsync(SystemOneMatchProbabilityRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Probability);
        }
    }
    private sealed class MediaStream(byte[] content) : IJSStreamReference
    {
        public byte[] Content => content;
        public long Length { get; set; } = content.Length;
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512_000, CancellationToken cancellationToken = default)
        {
            Reads++;
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Length <= maxAllowedSize);
            return ValueTask.FromResult<Stream>(new MemoryStream(content));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

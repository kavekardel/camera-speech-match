# Camera speech match

A .NET 10 Blazor component that records a person reading a prompt, checks face visibility in the camera preview, and compares a speech transcript with the prompt. The UI uses MudBlazor 8, and the repository includes a Blazor Interactive Server sample.

The host supplies speech-to-text through `ISttService` and, for semantic matching, a probability through `ISystemOneService`. The sample implements both using Intra's STT gateway protocols. Completed processing returns the recorded video bytes and optional PCM audio in `CameraSpeechMatchResult`. Persian and English are supported.

See the [project index](docs/AGENT.md) for the source map, execution flow, integration boundaries, and current implementation gaps.

## Contents

- [Recording flow](#recording-flow)
- [Requirements](#requirements)
- [Run the sample](#run-the-sample)
- [Use the component](#use-the-component)
- [Service contracts](#service-contracts)
- [Matching behavior](#matching-behavior)
- [Component parameters](#component-parameters)
- [Result contract](#result-contract)
- [Optional live captions](#optional-live-captions)
- [Troubleshooting](#troubleshooting)
- [Development and assets](#development-and-assets)

## Recording flow

1. The user selects **Start recording** and allows camera and microphone access.
2. Recording starts automatically once a fresh detection shows the full detected face inside the visible preview. Centering and face size provide positioning guidance; they do not determine acceptance.
3. The user reads the prompt and stops recording, or the configured duration expires.
4. Face validation checks the recording interval and its end. A visibility loss or gap in fresh detections of one second or more fails the attempt. The face must also be visible at the end.
5. The component transfers the recorded media and, when enabled, separately captured PCM audio to .NET and calls `ISttService.TranscribeAsync`. This service's transcript determines the match, including when live captions are enabled.
6. Lexical mode compares normalized wording similarity with its configured minimum percentage. Semantic mode converts the probability from `ISystemOneService` to a percentage and compares it with its configured minimum percentage.
7. `OnResult` reports completed processing or an attempt failure. **Record again** clears the attempt and returns to the initial screen.

Face detection runs locally in a Web Worker using bundled MediaPipe assets. These checks establish face visibility; the implementation does not perform identity recognition or liveness detection.

## Requirements

- .NET 10 SDK to build and run the projects.
- A camera, microphone, and browser permission to access them.
- A secure browser context: use HTTPS when deployed. The sample uses `http://localhost:5295` for local development.
- Browser support for `getUserMedia`, `MediaRecorder` with WebM or MP4, Web Workers, `createImageBitmap`, `OffscreenCanvas`, and WebAssembly.
- A working host implementation of `ISttService` and a registered `ISystemOneService`. Both interfaces are constructor dependencies, including in Lexical mode.
- For PCM capture or live captions, browser support for `AudioContext` and `AudioWorkletNode`. The sample enables PCM capture.
- The sample needs access to its configured STT gateway. Live captions additionally require an external WebSocket relay.

The library targets `net10.0`, references `Microsoft.AspNetCore.Components.Web` 10.0.11, and references MudBlazor using the floating version `8.*`. There is no npm build step; JavaScript and MediaPipe assets are included under the library's `wwwroot`.

## Run the sample

The sample registers [SttService](src/CameraSpeechMatch.Sample/Services/Stt/SttService.cs) and [SystemOneService](src/CameraSpeechMatch.Sample/Services/SystemOne/SystemOneService.cs). `SttService` handles the WebSocket protocol directly in one class. The sample enables `CapturePcmAudio`, uses `en-US` for its initial English prompt, and leaves live captions disabled. No FFmpeg, file-upload transcription endpoint, or sample relay is needed.

Configuration lives in [appsettings.json](src/CameraSpeechMatch.Sample/appsettings.json). The default gateway matches Intra: `https://ai-stt-gateway.airparsiana.com`. Override it in `appsettings.Development.json` or with `SttGateway__BaseUrl` to use another compatible gateway. Service credentials, if required by a different deployment, should remain server-side; this implementation follows Intra's client, which does not add an authentication header.

| Setting | Default | Purpose |
| --- | --- | --- |
| `SttGateway:BaseUrl` | Intra gateway URL | Shared destination for STT and SystemOne; use HTTP(S) to support both. Path prefixes are preserved. |
| `SttGateway:ConnectTimeoutSeconds` | `10` | STT WebSocket connection deadline. |
| `SttGateway:MaximumEventBytes` | `65536` | Maximum assembled STT event size, including fragmented frames. |
| `SpeechInput:Enabled` | `true` | Enable sample transcription. |
| `SpeechInput:ReadyTimeoutSeconds` | `15` | Wait for gateway readiness. |
| `SpeechInput:FinalizationTimeoutSeconds` | `6` | Wait for completion after sending audio. |
| `SpeechInput:MaximumSessionSeconds` | `300` | Overall transcription deadline, including connection and audio delivery. |
| `SpeechInput:MaximumAudioBytes` | `10485760` | PCM byte limit, capped at 10 MiB. |
| `SystemOne:Enabled` | `true` | Enable semantic comparison. |
| `SystemOne:TimeoutSeconds` | `35` | HTTP deadline, including response-body reading. |
| `SystemOne:MaxRequestBytes` | `65536` | Maximum serialized boolean request size. |
| `SystemOne:MaxResponseBytes` | `262144` | Maximum boolean response size. |

The sample sends captured PCM through `/api/stt/v1/stream` in 40 ms chunks at the recording's audio rate, matching Intra's live-stream transport. Transcription starts after recording, so processing takes approximately the recording duration plus gateway finalization. The sample's normal recording limit is 30 seconds. For longer recordings, increase `SpeechInput:MaximumSessionSeconds` (up to 600) to leave time for connection, audio delivery, and finalization; a recording near 300 seconds can exhaust the default budget.

From the repository root:

```bash
dotnet restore CameraSpeechMatch.slnx
dotnet build CameraSpeechMatch.slnx --configuration Release --no-restore
dotnet run --project src/CameraSpeechMatch.Sample --configuration Release --no-build --launch-profile http
```

Open `http://localhost:5295`. The page lets you edit the prompt, select a match mode, and inspect the latest result and recorded playback. To test Persian, use Persian prompt text and change `Language` in [Home.razor](src/CameraSpeechMatch.Sample/Components/Pages/Home.razor) to `fa-IR`.

## Use the component

Reference [CameraSpeechMatch.csproj](src/CameraSpeechMatch/CameraSpeechMatch.csproj) from your Blazor host and add `@using CameraSpeechMatch` to the host's `_Imports.razor`.

Register MudBlazor and the two service implementations in the host's `Program.cs`:

```csharp
using CameraSpeechMatch;
using MudBlazor.Services;

builder.Services.AddMudServices();
builder.Services.AddScoped<ISttService, YourSttService>();
builder.Services.AddScoped<ISystemOneService, UnavailableSystemOneService>();
```

`YourSttService` is your implementation of the [transcription contract](#speech-to-text). For Semantic mode, replace `UnavailableSystemOneService` with your working matcher. Choose service lifetimes to suit your provider implementation; the sample uses a singleton transcription service.

The repository does not define an `AddCameraSpeechMatch` registration extension. Register the interfaces directly.

For an Interactive Server host, enable `AddInteractiveServerComponents()` and `AddInteractiveServerRenderMode()`, map static assets, and render the page or routes interactively. Use the sample's [Program.cs](src/CameraSpeechMatch.Sample/Program.cs) and [App.razor](src/CameraSpeechMatch.Sample/Components/App.razor) as references. Include MudBlazor's stylesheet and script, and the host's generated `<HostAssembly>.styles.css` so the component's isolated styles load. The sample [layout](src/CameraSpeechMatch.Sample/Components/Layout/MainLayout.razor) also shows the MudBlazor providers.

Example page for a host configured with interactive routes:

```razor
@page "/verify"
@using CameraSpeechMatch

<CameraSpeechMatch ExpectedText="This is a sample text."
                   Language="en-US"
                   CapturePcmAudio="true"
                   MatchMode="CameraSpeechMatchMode.Lexical"
                   MaxRecordingSeconds="30"
                   FileNamePrefix="verification"
                   ShowResultDetails="true"
                   OnResult="HandleResult"
                   OnReset="ClearResult" />

@if (lastResult is { } result)
{
    <p>Processing completed: @result.Succeeded. Lexical similarity: @result.LexicalSimilarityPercent%.</p>
}

@code {
    private CameraSpeechMatchResult? lastResult;

    private Task HandleResult(CameraSpeechMatchResult result)
    {
        lastResult = result;
        // Check Succeeded and evaluate the returned match data before accepting the verification.
        // VideoContent contains the recording when processing succeeds.
        return Task.CompletedTask;
    }

    private void ClearResult() => lastResult = null;
}
```

### Lexical mode service registration

Create `UnavailableSystemOneService.cs` in the host project with this implementation when semantic matching is disabled:

```csharp
using CameraSpeechMatch;

public sealed class UnavailableSystemOneService : ISystemOneService
{
    public bool IsAvailable => false;

    public Task<double> MatchProbabilityAsync(
        SystemOneMatchProbabilityRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<double>(
            new InvalidOperationException("Semantic matching is not configured."));
}
```

Lexical mode does not call this matcher. Selecting Semantic mode with this registration produces an availability error before the camera starts.

## Service contracts

### Speech-to-text

Implement [ISttService](src/CameraSpeechMatch/ISttService.cs):

```csharp
Task<string> TranscribeAsync(
    SttTranscribeRequest request,
    CancellationToken cancellationToken);
```

Set `IsAvailable` according to whether your service is configured. The component passes a [SttTranscribeRequest](src/CameraSpeechMatch/SttTranscribeRequest.cs) with:

| Property | Value supplied by the component |
| --- | --- |
| `Language` | `fa` or `en`, normalized from the component's language setting. |
| `PcmAudioContent` | Optional raw mono 16 kHz signed PCM16 little-endian bytes, populated when `CapturePcmAudio` is enabled. |

The sample STT service requires `PcmAudioContent`. It waits for `ready`, delivers audio while reading events concurrently, sends `complete`, and returns accumulated final transcript fragments only after `completed`. Interim captions do not determine the result.

The transcription request contains only language and PCM audio. Enable `CapturePcmAudio="true"` to supply audio to your provider. Recorded video is returned separately in `CameraSpeechMatchResult`.

Return the recognized text and honor cancellation. The component trims the returned text and fails the attempt if it is empty. The sample's `SttService` connects directly to the external STT gateway; the optional caption relay also runs externally.

### Semantic matcher

Implement [ISystemOneService](src/CameraSpeechMatch/ISystemOneService.cs) and register it with `IsAvailable == true`:

```csharp
Task<double> MatchProbabilityAsync(
    SystemOneMatchProbabilityRequest request,
    CancellationToken cancellationToken);
```

The [request](src/CameraSpeechMatch/SystemOneMatchProbabilityRequest.cs) includes `ExpectedText`, `Transcript`, `Task`, `Rules`, `TrueCriterion`, and `FalseCriterion`. The component supplies instructions to allow paraphrasing while preserving essential facts, names, identifiers, numbers, dates, and affirmation or negation. It also asks the matcher to treat both texts as comparison data.

Return a finite probability in `[0, 1]`. Invalid probabilities and handled provider errors fail processing. The component multiplies the probability by 100 to obtain `SemanticSimilarityPercent` and accepts it when that percentage is greater than or equal to `MinimumSemanticSimilarityPercent`.

The sample posts to `/api/system-one/v1/boolean` using Intra's contract: `state` contains the transcript, `instructions` contains the task/prompt/rules, and `criteria` contains true/false guidance. It requires HTTP 200, strict valid JSON, a finite probability in `[0, 1]`, and valid token-usage metadata. Logging records sizes, timings, and usage rather than recording content or comparison text.

## Matching behavior

**Lexical mode** uses [SpeechTranscriptComparer](src/CameraSpeechMatch/SpeechTranscriptComparer.cs). It applies Unicode compatibility normalization (Form KC), normalizes selected Arabic/Persian letter variants, converts Persian and Arabic digits to ASCII digits, lowercases invariantly, and turns runs of non-letter/digit characters into a single separator. Acceptance requires `LexicalSimilarityPercent >= MinimumLexicalSimilarityPercent`; the default minimum is `100`.

For example, `Hello, WORLD!` and `hello world` score 100%; Persian `۱۲۳` and ASCII `123` score 100%. Lowering the minimum permits partial wording matches.

`LexicalSimilarityPercent` measures Levenshtein similarity of normalized word sequences. When both strings contain one word, it compares Unicode characters instead. The score is rounded to two decimal places and is also reported in Semantic mode, independently of semantic acceptance.

**Semantic mode** uses the host's probability service. It converts the probability to a 0–100 percentage and accepts scores at or above `MinimumSemanticSimilarityPercent`, which defaults to `80`. Each mode uses its own minimum.

## Component parameters

| Parameter | Default | Behavior |
| --- | --- | --- |
| `ExpectedText` | Empty string | Required prompt. Blank text prevents recording. |
| `Language` | `null` → `fa` | Only Persian and English; region suffixes such as `fa-IR` and `en-US` are accepted. |
| `MatchMode` | `Lexical` | `Lexical` or `Semantic`. |
| `MinimumLexicalSimilarityPercent` | `100` | Lexical similarity minimum; must be finite and between 0 and 100. |
| `MinimumSemanticSimilarityPercent` | `80` | Semantic similarity minimum; must be finite and between 0 and 100. |
| `MaxRecordingSeconds` | `30` | Clamped to 1–300 seconds. |
| `FileNamePrefix` | `null` | Prefix for video and audio file names. Defaults to `camera-speech-match`; keeps letters, digits, `-`, and `_`, up to 100 characters. |
| `SpeechRelayUrl` | `null` | Optional live-caption WebSocket address or host-relative path. Blank disables captions. |
| `CapturePcmAudio` | `false` | Captures mono 16 kHz PCM16 alongside video, flushes the final partial chunk, and supplies `PcmAudioContent` to the transcription request and successful result. Requires AudioWorklet support. |
| `ShowResultDetails` | `false` | Shows the successful match details when enabled. Failure messages are displayed regardless. |
| `CenterToleranceX` | `0.12` | Horizontal centering guidance as a preview fraction; outside 0–0.5 falls back to the default. |
| `CenterToleranceY` | `0.12` | Vertical centering guidance as a preview fraction; outside 0–0.5 falls back to the default. |
| `MinFaceSize` | `0.25` | Minimum face height for positioning guidance, as a preview-height fraction. |
| `MaxFaceSize` | `0.65` | Maximum face height for positioning guidance. Invalid size ranges revert to both defaults; require `0 < MinFaceSize <= MaxFaceSize <= 1`. |
| `OnResult` | Unset | `EventCallback<CameraSpeechMatchResult>` for completed processing or attempt failure. |
| `OnReset` | Unset | `EventCallback` when attempt state is cleared, including before camera startup and on retry. |

The component accepts video recordings up to **100 MiB** and optional PCM audio up to **10 MiB**. Face validation must pass before either stream is read. It buffers the complete media in memory before calling the transcription service. PCM capture resources are released on stop, failure, retry, disposal, and navigation.

## Result contract

`Succeeded` means processing finished, including completed mismatches. The result contains transcript text, lexical similarity, and optional semantic similarity, plus the minimum captured for the selected mode. All similarity scores and minimums use a 0–100 percentage scale. The component uses its own comparison flag to display match status. For host acceptance, first check `Succeeded`, then compare `LexicalSimilarityPercent` with `MinimumLexicalSimilarityPercent` in Lexical mode or `SemanticSimilarityPercent` with `MinimumSemanticSimilarityPercent` in Semantic mode.

| Property | Meaning |
| --- | --- |
| `Succeeded` | Whether recording processing completed successfully. |
| `MatchMode` | Mode used for this result. |
| `LexicalSimilarityPercent` | Normalized wording similarity from 0 to 100, also reported in Semantic mode. |
| `MinimumLexicalSimilarityPercent` | Minimum percentage for a Lexical attempt; `null` in Semantic mode or when the configured minimum is invalid. |
| `SemanticSimilarityPercent` | Provider probability multiplied by 100; `null` in Lexical mode. |
| `MinimumSemanticSimilarityPercent` | Minimum percentage for a Semantic attempt; `null` in Lexical mode or when the configured minimum is invalid. |
| `ExpectedText`, `TranscribedText` | Prompt and the final transcript returned by `ISttService`. On failure, the transcript may be empty or partial. |
| `VideoContent` | Recording bytes on successful processing; cleared to `null` on attempt failure. |
| `VideoContentType` | `video/webm` or `video/mp4`. |
| `VideoFileName` | Sanitized prefix, UTC timestamp, and `.webm` or `.mp4` extension. |
| `VideoContentLength` | Byte count of the recording. |
| `PcmAudioContent` | Raw mono 16 kHz signed PCM16 little-endian bytes from the recording interval, up to 10 MiB. `null` when capture is disabled or processing fails. |
| `PcmAudioContentType` | `application/octet-stream`; `null` when no audio metadata is available. |
| `PcmAudioFileName` | Same sanitized prefix and UTC timestamp as the video, with a `.pcm` extension. `null` when no audio metadata is available. |
| `PcmAudioContentLength` | Captured PCM byte count; `0` when no audio metadata is available. |
| `ErrorMessage` | Failure explanation, otherwise `null`. |

PCM is captured from the same microphone stream during video recording. The successful result reuses the audio bytes supplied to `ISttService`; they can be sent as `application/octet-stream` to a PCM transcription endpoint.

If semantic processing fails after transcription, video and audio metadata can remain populated while both `VideoContent` and `PcmAudioContent` are cleared. Always inspect `Succeeded` before consuming recording data. The component returns bytes to the host; saving or uploading them is the host's responsibility.

## Optional live captions

Leave `SpeechRelayUrl` blank to transcribe only after recording through `ISttService`. Set it when you have a compatible external relay. Host-relative paths use the current host and choose `ws:` or `wss:` to match the page; explicit WebSocket URLs and HTTP(S) URLs are also accepted.

The browser opens the socket and sends this initial JSON message, followed by binary mono, 16 kHz, signed 16-bit little-endian PCM audio chunks:

```json
{"language":"en","audioFormat":"pcm_s16le","sampleRate":16000,"channels":1}
```

The relay sends JSON messages:

| Type | Example | Purpose |
| --- | --- | --- |
| `ready` | `{"type":"ready"}` | Provider is ready. |
| `transcript` | `{"type":"transcript","text":"Hello","isFinal":false}` | Interim caption, replaced by subsequent interim text. |
| `transcript` | `{"type":"transcript","text":"Hello world","isFinal":true,"utteranceFinal":true}` | Final fragment appended to committed captions. |
| `error` | `{"type":"error","code":"capacity_reached"}` | Fails the recording attempt with a mapped error message. |
| `completed` | `{"type":"completed"}` | Confirms relay finalization. |

At recording end, the browser sends `{"type":"complete"}` and waits for `completed`. Default readiness and completion timeouts are 15 seconds and 6 seconds, respectively. Once enabled, relay errors or missing completion fail the attempt; the relay does not replace the required post-recording transcription service.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| A custom host fails to construct the component | Register both service interfaces; `ISystemOneService` is required even for Lexical mode. The sample already registers both. |
| Sample reports missing PCM audio | Enable `CapturePcmAudio` on the component; the sample gateway service requires it. |
| “Speech recognition is not configured” | Check the registered `ISttService.IsAvailable`. |
| Semantic matching is unavailable | Register a working `ISystemOneService` with `IsAvailable == true`. |
| Camera or microphone cannot start | Check permissions, a secure context, device availability, and browser recording support. |
| Face detection is unavailable | Check Worker/OffscreenCanvas/WASM support and loading of the library's MediaPipe assets. |
| Face validation fails | Keep the full detected face inside the visible preview, keep the tab visible, and maintain fresh detections through recording end. |
| Captions time out or fail to finalize | Check the relay URL, protocol, `ready`, and `completed` messages. |
| Transcript is empty or inaccurate | Check the provider's media support and whether `Language` matches the spoken prompt. |
| Sample transcription times out | Check gateway connectivity and readiness/completion messages. Include audio delivery time in the overall transcription budget. |
| Component appears without its styles | Include MudBlazor assets and the host's generated scoped stylesheet. |
| Build reports locked sample DLLs | Stop the running sample before rebuilding the same configuration, or build another configuration. |

## Development and assets

The solution includes xUnit/bUnit tests for the sample services and component. Run:

```bash
dotnet build CameraSpeechMatch.slnx --configuration Release
dotnet test CameraSpeechMatch.slnx --configuration Release
```

.NET tests cover STT protocol ordering/limits/deadlines, actual loopback WebSocket framing, SystemOne mapping/validation, and component success/mismatch/failure behavior, including lexical and semantic minimums.

MediaPipe assets are bundled in [wwwroot/vendor/mediapipe/1.0.1](src/CameraSpeechMatch/wwwroot/vendor/mediapipe/1.0.1). Their [manifest](src/CameraSpeechMatch/wwwroot/vendor/mediapipe/1.0.1/asset-manifest.json) records versions, source locations, sizes, and SHA-256 hashes; the included [license](src/CameraSpeechMatch/wwwroot/vendor/mediapipe/1.0.1/LICENSE) identifies Apache-2.0. The library maps `.task` assets to `application/octet-stream`.

For module responsibilities, change locations, and analysis findings, see [docs/AGENT.md](docs/AGENT.md).

# Camera speech match

A .NET 10 Blazor component that records a person reading a prompt, checks face visibility in the camera preview, and compares a speech transcript with the prompt. The UI uses MudBlazor 8, and the repository includes a Blazor Interactive Server sample.

The host supplies speech-to-text through `ISttService` and, for semantic matching, a probability through `ISystemOneService`. The sample implements both locally through Ollama, using `frozenlab/qwen3-asr:0.6b` for transcription and `nimble:9b-q4_K_M` for semantic decisions. Completed processing returns the recorded video bytes and automatically captured PCM audio in `CameraSpeechMatchResult`. The host's transcription service selects the language; the local Sample uses automatic language detection.

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
- [Troubleshooting](#troubleshooting)
- [Development and assets](#development-and-assets)

## Recording flow

1. The user selects **Start recording** and allows camera and microphone access.
2. Recording starts automatically once a fresh detection shows the full detected face inside the visible preview. Centering and face size provide positioning guidance; they do not determine acceptance.
3. The user reads the prompt and stops recording, or the configured duration expires.
4. Face validation checks the recording interval and its end. A visibility loss or gap in fresh detections of one second or more fails the attempt. The face must also be visible at the end.
5. The component transfers the recorded video and separately captured PCM audio to .NET and calls `ISttService.TranscribeAsync`. This service's transcript determines the match.
6. Lexical mode checks normalized wording similarity. Semantic mode checks the probability from `ISystemOneService`, converted to a percentage. Combined mode requires both minimums and calls the semantic provider only after the lexical minimum passes.
7. `OnResult` reports completed processing or an attempt failure. **Record again** clears the attempt and returns to the initial screen.

Face detection runs locally in a Web Worker using bundled MediaPipe assets. These checks establish face visibility; the implementation does not perform identity recognition or liveness detection.

## Requirements

- .NET 10 SDK to build and run the projects.
- A camera, microphone, and browser permission to access them.
- A secure browser context: use HTTPS when deployed. The sample uses `http://localhost:5295` for local development.
- Browser support for `getUserMedia`, `MediaRecorder` with WebM or MP4, Web Workers, `createImageBitmap`, `OffscreenCanvas`, and WebAssembly.
- A working host implementation of `ISttService` and a registered `ISystemOneService`. Both interfaces are constructor dependencies, including in Lexical mode.
- Browser support for `AudioContext` and `AudioWorkletNode` to capture PCM audio alongside every video recording.
- The sample needs local Ollama and both configured models; see the [Sample setup](src/CameraSpeechMatch.Sample/README.md). No Docker or API keys are required.

The library targets `net10.0`, references `Microsoft.AspNetCore.Components.Web` 10.0.11, and references MudBlazor using the floating version `8.*`. There is no npm build step; JavaScript and MediaPipe assets are included under the library's `wwwroot`.

## Run the sample

The Sample uses the host's installed Ollama server and models. From the repository root:

```sh
dotnet run --project src/CameraSpeechMatch.AppHost/CameraSpeechMatch.AppHost.csproj
```

Open the local Aspire dashboard at `http://localhost:15295`, then the `sample` resource's endpoint. The local launch profile enables anonymous dashboard access, so no login token is required. Aspire 13.6.1 runs the Sample directly on the host and references existing Ollama at `http://localhost:11434`; it does not create containers or pull models. Startup checks both model names and required capabilities within a bounded deadline. The recorder becomes available after readiness succeeds.

See the [Sample README](src/CameraSpeechMatch.Sample/README.md) for prerequisites, installation checks, configuration, exact model protocols, troubleshooting, stop instructions, and opt-in audio integration tests. The Sample uses only local Ollama models.

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

`YourSttService` is your implementation of the [transcription contract](#speech-to-text). For Semantic or Combined mode, replace `UnavailableSystemOneService` with your working matcher. Choose service lifetimes to suit your provider implementation; the Sample uses typed HTTP clients.

The repository does not define an `AddCameraSpeechMatch` registration extension. Register the interfaces directly.

For an Interactive Server host, enable `AddInteractiveServerComponents()` and `AddInteractiveServerRenderMode()`, map static assets, and render the page or routes interactively. Use the sample's [Program.cs](src/CameraSpeechMatch.Sample/Program.cs) and [App.razor](src/CameraSpeechMatch.Sample/Components/App.razor) as references. Include MudBlazor's stylesheet and script, and the host's generated `<HostAssembly>.styles.css` so the component's isolated styles load. The sample [layout](src/CameraSpeechMatch.Sample/Components/Layout/MainLayout.razor) also shows the MudBlazor providers.

Example page for a host configured with interactive routes:

```razor
@page "/verify"
@using CameraSpeechMatch

<CameraSpeechMatch ExpectedText="This is a sample text."
                   MatchMode="CameraSpeechMatchMode.Lexical"
                   MaxRecordingSeconds="30"
                   FileNamePrefix="verification"
                   ShowResultDetails="true"
                   OnResult="HandleResult"
                   OnReset="ClearResult" />

@if (lastResult is { } result)
{
    <p>Processing completed: @result.Succeeded. Accepted: @result.IsAccepted. Lexical similarity: @result.LexicalSimilarityPercent%.</p>
}

@code {
    private CameraSpeechMatchResult? lastResult;

    private Task HandleResult(CameraSpeechMatchResult result)
    {
        lastResult = result;
        // IsAccepted is true only when processing succeeded and the selected mode's minimums passed.
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
        MatchProbabilityRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<double>(
            new InvalidOperationException("Semantic matching is not configured."));
}
```

Lexical mode does not call this matcher. Selecting Semantic or Combined mode with this registration produces an availability error before the camera starts.

## Service contracts

### Speech-to-text

Implement [ISttService](src/CameraSpeechMatch/ISttService.cs):

```csharp
Task<string> TranscribeAsync(
    TranscribeRequest request,
    CancellationToken cancellationToken);
```

Set `IsAvailable` according to whether your service is configured. The component passes a [TranscribeRequest](src/CameraSpeechMatch/TranscribeRequest.cs) with:

| Property | Value supplied by the component |
| --- | --- |
| `PcmAudioContent` | Raw mono 16 kHz signed PCM16 little-endian bytes, captured automatically alongside the video. |

The local Sample requires `PcmAudioContent`, wraps bounded windows in WAV headers in memory, sends audio through the ASR model's documented Ollama interface, and returns final transcript text. No audio-conversion executable is required.

The transcription request contains only automatically captured PCM audio. Your service selects the transcription language through its own configuration or provider language detection. The component's recording flow is independent of language. Recorded video and PCM audio are returned in `CameraSpeechMatchResult`.

Return the recognized text and honor cancellation. The component trims the returned text and fails the attempt if it is empty. The default Sample calls only its configured local Ollama server.

### Semantic matcher

Implement [ISystemOneService](src/CameraSpeechMatch/ISystemOneService.cs) and register it with `IsAvailable == true`:

```csharp
Task<double> MatchProbabilityAsync(
    MatchProbabilityRequest request,
    CancellationToken cancellationToken);
```

The [request](src/CameraSpeechMatch/MatchProbabilityRequest.cs) represents one System One `noul` decision: `State`, `Instructions`, and optional string properties `TrueCriteria` and `FalseCriteria`. State and instructions accept a nonblank string or a JSON-serializable object or array. A null criterion uses the model's default for that outcome. The adapter maps these typed properties to the standard `criteria.true` and `criteria.false` fields. See the [Ollama API reference](https://docs.ollama.com/api/systemone).

```csharp
var request = new MatchProbabilityRequest
{
    State = new { expectedText = "I approve payment of 100 dollars.", transcript = "I approve payment of 100 dollars." },
    Instructions = "Does transcript preserve the meaning and every essential fact of expectedText?",
    TrueCriteria = "Equivalent meaning and facts.",
    FalseCriteria = "Different meaning, changed or missing facts, or insufficient evidence."
};
```

The component builds the phrase data and comparison guidance before calling the service. Its instructions allow paraphrasing while preserving essential facts, names, identifiers, numbers, dates, and affirmation or negation, and require treating both phrases as data. It rejects empty transcriptions before calling the matcher. The Ollama adapter preserves state, instructions, and criterion descriptions, wrapping them in one `questions.match` entry with `type: "noul"` and mapping the typed criteria to their wire fields; model selection stays in configuration.

Return a finite probability in `[0, 1]`. Invalid probabilities and handled provider errors fail processing. The component multiplies the probability by 100 to obtain `SemanticSimilarityPercent`. The semantic minimum passes when that percentage is greater than or equal to `MinimumSemanticSimilarityPercent`.

The local Sample posts structured phrase data and a typed `noul` question to Ollama's `/v1/systemone` endpoint. It validates the returned probability and rejects uncertain decisions before applying the component's acceptance threshold. See the [Sample README](src/CameraSpeechMatch.Sample/README.md) for the protocol and confidence settings.

## Matching behavior

**Lexical mode** uses [SpeechTranscriptComparer](src/CameraSpeechMatch/SpeechTranscriptComparer.cs). It applies Unicode compatibility normalization (Form KC), normalizes selected Arabic/Persian letter variants, converts Persian and Arabic digits to ASCII digits, lowercases invariantly, and turns runs of non-letter/digit characters into a single separator. Acceptance requires `LexicalSimilarityPercent >= MinimumLexicalSimilarityPercent`; the default minimum is `100`.

For example, `Hello, WORLD!` and `hello world` score 100%; Persian `۱۲۳` and ASCII `123` score 100%. Lowering the minimum permits partial wording matches.

`LexicalSimilarityPercent` measures Levenshtein similarity of normalized word sequences. When both strings contain one word, it compares Unicode characters instead. The score is rounded to two decimal places and is also reported in Semantic mode, independently of semantic acceptance.

**Semantic mode** uses the host's probability service. It converts the probability to a 0–100 percentage and accepts scores at or above `MinimumSemanticSimilarityPercent`, which defaults to `80`. Each mode uses its own minimum.

**Combined mode** first compares lexical similarity with `MinimumLexicalSimilarityPercent`. If it fails, processing finishes with `IsAccepted = false` and `SemanticSimilarityPercent = null`, without calling the semantic provider. If it passes, the component calculates semantic similarity and checks `MinimumSemanticSimilarityPercent`. Both scores must meet their respective minimums for acceptance.

| Mode | Acceptance rule |
| --- | --- |
| `Lexical` | `LexicalSimilarityPercent >= MinimumLexicalSimilarityPercent` |
| `Semantic` | `SemanticSimilarityPercent >= MinimumSemanticSimilarityPercent` |
| `Combined` | Both comparisons above pass. |

Equality passes in every mode. Active minimums must be finite and within 0–100; Combined validates both and requires an available semantic provider before recording. The sample shows both minimum inputs when Combined is selected and explains when semantic evaluation was skipped.

## Component parameters

| Parameter | Default | Behavior |
| --- | --- | --- |
| `ExpectedText` | Empty string | Required prompt. Blank text prevents recording. |
| `MatchMode` | `Lexical` | `Lexical`, `Semantic`, or `Combined`. |
| `MinimumLexicalSimilarityPercent` | `100` | Inclusive lexical similarity minimum in Lexical and Combined modes; must be finite and between 0 and 100. |
| `MinimumSemanticSimilarityPercent` | `80` | Inclusive semantic similarity minimum in Semantic and Combined modes; must be finite and between 0 and 100. |
| `MaxRecordingSeconds` | `30` | Clamped to 1–300 seconds. |
| `FileNamePrefix` | `null` | Prefix for video and audio file names. Defaults to `camera-speech-match`; keeps letters, digits, `-`, and `_`, up to 100 characters. |
| `ShowResultDetails` | `false` | Shows the successful match details when enabled. Failure messages are displayed regardless. |
| `CenterToleranceX` | `0.12` | Horizontal centering guidance as a preview fraction; outside 0–0.5 falls back to the default. |
| `CenterToleranceY` | `0.12` | Vertical centering guidance as a preview fraction; outside 0–0.5 falls back to the default. |
| `MinFaceSize` | `0.25` | Minimum face height for positioning guidance, as a preview-height fraction. |
| `MaxFaceSize` | `0.65` | Maximum face height for positioning guidance. Invalid size ranges revert to both defaults; require `0 < MinFaceSize <= MaxFaceSize <= 1`. |
| `OnResult` | Unset | `EventCallback<CameraSpeechMatchResult>` for completed processing or attempt failure. |
| `OnReset` | Unset | `EventCallback` when attempt state is cleared, including before camera startup and on retry. |

The component automatically captures mono 16 kHz PCM16 alongside video and flushes the final partial audio chunk. It accepts video recordings up to **100 MiB** and PCM audio up to **10 MiB**. Face validation must pass before either stream is read. It buffers the complete media in memory before calling the transcription service. PCM capture resources are released on stop, failure, retry, disposal, and navigation.

## Result contract

`Succeeded` means processing finished, including completed mismatches. `IsAccepted` is the final acceptance decision used by the component and host: it is true only when processing succeeded and every minimum required by the selected mode passed. A completed rejection has `Succeeded = true`, `IsAccepted = false`, and retains its video and PCM bytes for playback or host use. Processing failures set both flags to false and clear recording bytes.

The result contains transcript text, lexical similarity, and optional semantic similarity, plus the active minimums captured for the attempt. Combined captures both minimums, including when semantic calculation is skipped. All similarity scores and minimums use a 0–100 percentage scale.

| Property | Meaning |
| --- | --- |
| `Succeeded` | Whether recording processing completed successfully. |
| `IsAccepted` | Whether processing succeeded and all minimums required by the selected mode passed. False for mismatches and processing failures. |
| `MatchMode` | Mode used for this result. |
| `LexicalSimilarityPercent` | Normalized wording similarity from 0 to 100, also reported in Semantic mode. |
| `MinimumLexicalSimilarityPercent` | Minimum percentage for a Lexical or Combined attempt; `null` in Semantic mode or when the configured minimum is invalid. |
| `SemanticSimilarityPercent` | Provider probability multiplied by 100; `null` when not evaluated, including Combined attempts that fail the lexical minimum. |
| `MinimumSemanticSimilarityPercent` | Minimum percentage for a Semantic or Combined attempt; `null` in Lexical mode or when the configured minimum is invalid. |
| `ExpectedText`, `TranscribedText` | Prompt and the final transcript returned by `ISttService`. On failure, the transcript may be empty or partial. |
| `VideoContent` | Recording bytes on successful processing; cleared to `null` on attempt failure. |
| `VideoContentType` | `video/webm` or `video/mp4`. |
| `VideoFileName` | Sanitized prefix, UTC timestamp, and `.webm` or `.mp4` extension. |
| `VideoContentLength` | Byte count of the recording. |
| `PcmAudioContent` | Raw mono 16 kHz signed PCM16 little-endian bytes captured automatically during recording, up to 10 MiB. `null` when processing fails. |
| `PcmAudioContentType` | `application/octet-stream`; `null` when no audio metadata is available. |
| `PcmAudioFileName` | Same sanitized prefix and UTC timestamp as the video, with a `.pcm` extension. `null` when no audio metadata is available. |
| `PcmAudioContentLength` | Captured PCM byte count; `0` when no audio metadata is available. |
| `ErrorMessage` | Failure explanation, otherwise `null`. |

PCM is captured from the same microphone stream during video recording. The successful result reuses the audio bytes supplied to `ISttService`; they can be sent as `application/octet-stream` to a PCM transcription endpoint.

If semantic processing fails after transcription, video and audio metadata can remain populated while both `VideoContent` and `PcmAudioContent` are cleared. Always inspect `Succeeded` before consuming recording data. The component returns bytes to the host; saving or uploading them is the host's responsibility.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| A custom host fails to construct the component | Register both service interfaces; `ISystemOneService` is required even for Lexical mode. The sample already registers both. |
| “Speech recognition is not configured” | Check the registered `ISttService.IsAvailable`. |
| Semantic matching is unavailable | Register a working `ISystemOneService` with `IsAvailable == true`. |
| Camera or microphone cannot start | Check permissions, a secure context, device availability, and browser recording support. |
| Face detection is unavailable | Check Worker/OffscreenCanvas/WASM support and loading of the library's MediaPipe assets. |
| Face validation fails | Keep the full detected face inside the visible preview, keep the tab visible, and maintain fresh detections through recording end. |
| Transcript is empty or inaccurate | Check the provider's media support and microphone input. The local Sample uses automatic language detection. |
| Sample transcription times out | Check local Ollama connectivity, `/health`, model availability, and configured inference deadlines. |
| Component appears without its styles | Include MudBlazor assets and the host's generated scoped stylesheet. |
| Build reports locked sample DLLs | Stop the running sample before rebuilding the same configuration, or build another configuration. |

## Development and assets

The solution includes xUnit/bUnit tests for the sample services and component. Run:

```bash
dotnet build CameraSpeechMatch.slnx --configuration Release
dotnet test CameraSpeechMatch.slnx --configuration Release
```

.NET tests cover Ollama WAV audio construction/windowing, strict ASR/Jev parsing, limits/deadlines/cancellation, uncertain decisions, bounded startup, configuration/DI, and component acceptance/mismatch/failure behavior. Component checks cover inclusive minimums, Combined short-circuiting, result flags, rejected-recording playback, provider availability/errors, readiness, and sample mode controls.

Real model inference and Aspire process orchestration have separate opt-in tests; see the [Sample README](src/CameraSpeechMatch.Sample/README.md#tests).

MediaPipe assets are bundled in [wwwroot/vendor/mediapipe/1.0.1](src/CameraSpeechMatch/wwwroot/vendor/mediapipe/1.0.1). Their [manifest](src/CameraSpeechMatch/wwwroot/vendor/mediapipe/1.0.1/asset-manifest.json) records versions, source locations, sizes, and SHA-256 hashes; the included [license](src/CameraSpeechMatch/wwwroot/vendor/mediapipe/1.0.1/LICENSE) identifies Apache-2.0. The library maps `.task` assets to `application/octet-stream`.

For module responsibilities, change locations, and analysis findings, see [docs/AGENT.md](docs/AGENT.md).

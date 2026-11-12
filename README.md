# Camera speech match

A .NET 10 Blazor component that records a person reading a prompt, checks face visibility in the camera preview, and compares a speech transcript with the prompt. The UI uses MudBlazor 8, and the repository includes a Blazor Interactive Server sample.

The library includes speech-to-text and semantic matching providers. Consumers configure Ollama, OpenAI-compatible services, Jev, Cloudflare Clef, Deepgram, or ElevenLabs through one DI registration. The Sample defaults to local Ollama with `frozenlab/qwen3-asr:0.6b` for transcription and `nimble:9b-q4_K_M` for semantic decisions. Existing `ISttService` and `ISystemOneService` implementations remain supported as optional overrides. Completed processing returns video and automatically captured PCM audio in `CameraSpeechMatchResult`; transcription supports provider language settings and automatic detection.

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
6. Lexical mode checks normalized wording similarity. Semantic mode checks the score from `ISystemOneService`, converted to a percentage. Combined mode requires both minimums and calls the semantic provider only after the lexical minimum passes.
7. `OnResult` reports completed processing or an attempt failure. **Record again** clears the attempt and returns to the initial screen.

Face detection runs locally in a Web Worker using bundled MediaPipe assets. These checks establish face visibility; the implementation does not perform identity recognition or liveness detection.

## Requirements

- .NET 10 SDK to build and run the projects.
- A camera, microphone, and browser permission to access them.
- A secure browser context: use HTTPS when deployed. The sample uses `http://localhost:5295` for local development.
- Browser support for `getUserMedia`, `MediaRecorder` with WebM or MP4, Web Workers, `createImageBitmap`, `OffscreenCanvas`, and WebAssembly.
- Server-side AI provider registration with working transcription, plus matching for Semantic/Combined mode. Optional host service overrides remain supported.
- Browser support for `AudioContext` and `AudioWorkletNode` to capture PCM audio alongside every video recording.
- The default sample configuration needs local Ollama and both configured models; see the [Sample setup](src/CameraSpeechMatch.Sample/README.md). No Docker or API keys are required.

The library targets `net10.0`, uses the ASP.NET Core shared framework, pins Microsoft.Extensions.AI packages at 10.11.0, and references MudBlazor using the floating version `8.*`. There is no npm build step; JavaScript and MediaPipe assets are included under the library's `wwwroot`.

## Run the sample

The Sample uses the host's installed Ollama server and models. From the repository root:

```sh
dotnet run --project src/CameraSpeechMatch.AppHost/CameraSpeechMatch.AppHost.csproj
```

Open the local Aspire dashboard at `http://localhost:15295`, then the `sample` resource's endpoint. The local launch profile enables anonymous dashboard access, so no login token is required. Aspire 13.6.1 runs the Sample directly on the host and references existing Ollama at `http://localhost:11434`; it does not create containers or pull models. Startup checks both model names and required capabilities within a bounded deadline. The recorder becomes available after readiness succeeds.

See the [Sample README](src/CameraSpeechMatch.Sample/README.md) for prerequisites, installation checks, configuration, exact model protocols, troubleshooting, stop instructions, and opt-in audio integration tests. The Sample defaults to local Ollama; provider configuration also supports cloud and mixed setups.

## Use the component

Reference [CameraSpeechMatch.csproj](src/CameraSpeechMatch/CameraSpeechMatch.csproj) from your Blazor host and add `@using CameraSpeechMatch` to the host's `_Imports.razor`.

Register MudBlazor and the built-in AI services in the host's server-side `Program.cs`:

```csharp
using CameraSpeechMatch;
using MudBlazor.Services;

builder.Services.AddMudServices();
builder.Services.AddCameraSpeechMatchAI(
    builder.Configuration.GetSection(CameraSpeechMatchAiOptions.SectionName));
```

Both providers default to `None`. Configure only transcription for Lexical mode; Semantic and Combined modes also require matching. No custom interfaces or dummy matcher are required.

### Built-in provider configuration

Local Ollama configuration in `appsettings.json`:

```json
{
  "CameraSpeechMatch": {
    "AI": {
      "Transcription": {
        "Provider": "Ollama",
        "Endpoint": "http://localhost:11434",
        "Model": "frozenlab/qwen3-asr:0.6b"
      },
      "SemanticMatching": {
        "Provider": "OllamaSystemOne",
        "Endpoint": "http://localhost:11434",
        "Model": "nimble:9b-q4_K_M"
      }
    }
  }
}
```

For cloud or self-hosted OpenAI-compatible services, change the relevant operation's `Provider` to `OpenAICompatible`, set `Endpoint` to its API base URL (including `/v1`), and select its `Model` explicitly. Transcription requires the audio transcription API; semantic matching requires Chat Completions with strict JSON Schema structured outputs. Chat compatibility alone does not establish either capability.

Set each `ApiKey` through server-side configuration. For example, use environment variables `CameraSpeechMatch__AI__Transcription__ApiKey` and `CameraSpeechMatch__AI__SemanticMatching__ApiKey`, or user-secret keys `CameraSpeechMatch:AI:Transcription:ApiKey` and `CameraSpeechMatch:AI:SemanticMatching:ApiKey`. The official OpenAI endpoint requires a key; explicitly configured self-hosted endpoints may omit it, in which case no authorization header is sent. Keys never become component parameters or browser configuration.

Providers can be mixed. The action overload is useful for composing configuration with host-specific overrides:

```csharp
builder.Services.AddCameraSpeechMatchAI(options =>
{
    options.Transcription.Provider = TranscriptionProvider.Ollama;
    options.Transcription.Model = "frozenlab/qwen3-asr:0.6b";
    options.SemanticMatching.Provider = SemanticMatchingProvider.OpenAICompatible;
    options.SemanticMatching.Endpoint = "https://your-provider.example/v1";
    options.SemanticMatching.Model = "your-structured-output-model";
    options.SemanticMatching.ApiKey = builder.Configuration["MatchingApiKey"];
});
```

Each operation supports `TimeoutSeconds` (default 120, range 1–600), `MaximumResponseBytes` (default 262144, range 1024–1048576), and Ollama readiness settings `StartupTimeoutSeconds` (60, range 1–300), `ProbeTimeoutSeconds` (5, range 1–30), and `RetryDelaySeconds` (2, range 1–30). Transcription supports `MaximumAudioBytes` (default/cap 10485760); matching supports `MaximumMatchingRequestBytes` (default/cap 65536, minimum 1024). Native System One providers (Ollama, Jev, and Cloudflare) support `MinimumDecisionProbability` (default 0.8, range 0.5–1); a 0.5 decision is always uncertain. Empty or malformed results fail processing. No automatic provider fallback or inference retry occurs.

`CameraSpeechMatchAiReadiness` exposes per-operation status, `IsReadyFor(mode)`, `GetStatus(mode)`, and `Changed` notifications. Registration adds a `camera-speech-match-ai` health check: transcription must be ready, plus matching if configured. Ollama checks run in the background with a bounded startup budget; cloud readiness means configuration is valid and does not make paid inference calls. Actual inference can still fail. Custom service availability is sampled when the host starts.

Built-in providers run on the server. The library pins Microsoft.Extensions.AI and its OpenAI adapter at 10.11.0; experimental speech APIs are isolated internally behind `ISttService`.

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

### Jev, Cloudflare Clef, Deepgram, and ElevenLabs

| Provider value | Operation | Example model | Default endpoint base |
| --- | --- | --- | --- |
| `Jev` | Semantic matching | `jev-1.14.0` | `https://api.typesafe.ai` |
| `CloudflareClef` | Semantic matching | `clef-flash` or `clef` | `https://api.cloudflare.com/client/v4` |
| `Deepgram` | Transcription | `nova-3` | `https://api.deepgram.com` |
| `ElevenLabs` | Transcription | `scribe_v2` | `https://api.elevenlabs.io` |

Each hosted provider requires an explicit `Model` and `ApiKey`. Cloudflare also requires `SemanticMatching.AccountId` from the Workers AI dashboard. The adapter builds the account-specific model URL. Its API token needs Workers AI permissions. [Cloudflare setup](https://developers.cloudflare.com/workers-ai/get-started/rest-api/)

Jev with ElevenLabs can use this configuration:

```json
{
  "CameraSpeechMatch": {
    "AI": {
      "Transcription": {
        "Provider": "ElevenLabs",
        "Model": "scribe_v2"
      },
      "SemanticMatching": {
        "Provider": "Jev",
        "Model": "jev-1.14.0"
      }
    }
  }
}
```

Supply `CameraSpeechMatch__AI__Transcription__ApiKey` with the ElevenLabs key and `CameraSpeechMatch__AI__SemanticMatching__ApiKey` with the TypeSafe key through your server's secret configuration. Jev uses its native typed Noul API. The example selects the published Jev 1.14 model version. [Jev API](https://docs.typesafe.ai/api/system-one), [Jev models](https://docs.typesafe.ai/models)

To use Deepgram with the same Jev matcher, replace the transcription section with:

```json
{
  "Provider": "Deepgram",
  "Model": "nova-3",
  "Language": "fa"
}
```

`Transcription.Language` accepts a provider-supported code such as `fa` or `en`. Deepgram maps it to `language`, ElevenLabs to `language_code`, and OpenAI-compatible transcription to the SDK's language option. Omit it for automatic detection. Ollama ASR requires automatic detection and rejects an explicit `Language`.

For Persian Deepgram recordings, set `Language` to `fa`. Nova-3 lists Persian support, while the documented automatic language detection list omits Persian. ElevenLabs lists Persian support and predicts the language automatically when `Language` is omitted. Both services receive mono 16 kHz PCM16 wrapped in WAV. ElevenLabs requests speech text with audio-event tagging, diarization, and webhooks disabled. [Deepgram models](https://developers.deepgram.com/docs/models-languages-overview), [Deepgram detection](https://developers.deepgram.com/docs/language-detection), [ElevenLabs languages](https://elevenlabs.io/docs/overview/capabilities/speech-to-text)

For Cloudflare, replace the matching section with:

```json
{
  "Provider": "CloudflareClef",
  "Model": "clef-flash",
  "AccountId": "0123456789abcdef0123456789abcdef"
}
```

Replace the example account ID with your own and supply the Cloudflare API token through `SemanticMatching.ApiKey`. Jev and Cloudflare return native `noul` probabilities and apply `MinimumDecisionProbability` before the component applies its semantic acceptance minimum. Cloudflare requests have a conservative 16 KiB serialized JSON ceiling, or the configured `MaximumMatchingRequestBytes` if lower, to leave headroom for the model's context and decision template. Evaluate thresholds using representative English and Persian phrases for your selected model. [Cloudflare Clef Flash](https://developers.cloudflare.com/workers-ai/models/clef-flash/)

The existing `OllamaSystemOne` provider can select installed decision models such as `nimble:9b-q4_K_M`, `clef`, `clef-flash`, or `tev1:4b` through its `Model` setting. Ollama checks the configured model's decision capability at startup. Install the selected model explicitly using `ollama pull <model>`. [Clef](https://ollama.com/library/clef), [Tev1](https://ollama.com/library/tev1)

### Optional custom service overrides

Existing hosts registering `ISttService` and `ISystemOneService` continue working without the new setup method. To override one built-in operation, register your implementation before or after `AddCameraSpeechMatchAI`:

```csharp
builder.Services.AddScoped<ISttService, YourSttService>();
```

An overridden operation does not activate, validate, or probe its unused built-in provider. The interfaces and component result contract remain compatible. Lexical mode never calls the semantic matcher.

## Service contracts

### Speech-to-text

For an optional custom transcription service, implement [ISttService](src/CameraSpeechMatch/ISttService.cs):

```csharp
Task<string> TranscribeAsync(
    TranscribeRequest request,
    CancellationToken cancellationToken);
```

Set `IsAvailable` according to whether your service is configured. The component passes a [TranscribeRequest](src/CameraSpeechMatch/TranscribeRequest.cs) with:

| Property | Value supplied by the component |
| --- | --- |
| `PcmAudioContent` | Raw mono 16 kHz signed PCM16 little-endian bytes, captured automatically alongside the video. |

The built-in Ollama adapter requires `PcmAudioContent`, wraps bounded windows in WAV headers in memory, sends audio through the ASR model's documented Ollama interface, and returns final transcript text. No audio-conversion executable is required.

The transcription request contains only automatically captured PCM audio. Your service selects the transcription language through its own configuration or provider language detection. The component's recording flow is independent of language. Recorded video and PCM audio are returned in `CameraSpeechMatchResult`.

Return the recognized text and honor cancellation. The component trims the returned text and fails the attempt if it is empty. The default Sample calls only its configured local Ollama server.

### Semantic matcher

For an optional custom matcher, implement [ISystemOneService](src/CameraSpeechMatch/ISystemOneService.cs) and register it with `IsAvailable == true`:

```csharp
Task<double> MatchProbabilityAsync(
    MatchProbabilityRequest request,
    CancellationToken cancellationToken);
```

The [request](src/CameraSpeechMatch/MatchProbabilityRequest.cs) describes one semantic decision (mapped to a System One `noul` question by Ollama, Jev, and Cloudflare): `State`, `Instructions`, and optional string properties `TrueCriteria` and `FalseCriteria`. State and instructions accept a nonblank string or a JSON-serializable object or array. A null criterion uses the model's default for that outcome. The adapter maps these typed properties to the standard `criteria.true` and `criteria.false` fields. See the [Ollama API reference](https://docs.ollama.com/api/systemone).

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

Return a finite match estimate in `[0, 1]`. Chat-generated scores are distinct from System One probabilities; neither is calibrated verification accuracy, so evaluate thresholds for the chosen provider and phrases. Invalid probabilities and handled provider errors fail processing. The component multiplies the probability by 100 to obtain `SemanticSimilarityPercent`. The semantic minimum passes when that percentage is greater than or equal to `MinimumSemanticSimilarityPercent`.

The built-in Ollama adapter posts structured phrase data and a typed `noul` question to Ollama's `/v1/systemone` endpoint. It validates the returned probability and rejects uncertain decisions before applying the component's acceptance threshold. See the [Sample README](src/CameraSpeechMatch.Sample/README.md) for the protocol and confidence settings.

## Matching behavior

**Lexical mode** uses [SpeechTranscriptComparer](src/CameraSpeechMatch/SpeechTranscriptComparer.cs). It applies Unicode compatibility normalization (Form KC), normalizes selected Arabic/Persian letter variants, converts Persian and Arabic digits to ASCII digits, lowercases invariantly, and turns runs of non-letter/digit characters into a single separator. Acceptance requires `LexicalSimilarityPercent >= MinimumLexicalSimilarityPercent`; the default minimum is `100`.

For example, `Hello, WORLD!` and `hello world` score 100%; Persian `۱۲۳` and ASCII `123` score 100%. Lowering the minimum permits partial wording matches.

`LexicalSimilarityPercent` measures Levenshtein similarity of normalized word sequences. When both strings contain one word, it compares Unicode characters instead. The score is rounded to two decimal places and is also reported in Semantic mode, independently of semantic acceptance.

**Semantic mode** uses the configured semantic provider. It converts the probability to a 0–100 percentage and accepts scores at or above `MinimumSemanticSimilarityPercent`, which defaults to `80`. Each mode uses its own minimum.

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

.NET tests cover WAV audio construction/windowing, native Ollama/Jev/Cloudflare decisions, Deepgram and ElevenLabs HTTP uploads, OpenAI SDK calls, language settings, limits/deadlines/cancellation, uncertain decisions, startup, configuration/DI, and component acceptance/mismatch/failure behavior. Component checks cover inclusive minimums, Combined short-circuiting, result flags, rejected-recording playback, provider availability/errors, readiness, and sample mode controls.

Real model inference and Aspire process orchestration have separate opt-in tests; see the [Sample README](src/CameraSpeechMatch.Sample/README.md#tests).

MediaPipe assets are bundled in [wwwroot/vendor/mediapipe/1.0.1](src/CameraSpeechMatch/wwwroot/vendor/mediapipe/1.0.1). Their [manifest](src/CameraSpeechMatch/wwwroot/vendor/mediapipe/1.0.1/asset-manifest.json) records versions, source locations, sizes, and SHA-256 hashes; the included [license](src/CameraSpeechMatch/wwwroot/vendor/mediapipe/1.0.1/LICENSE) identifies Apache-2.0. The library maps `.task` assets to `application/octet-stream`.

For module responsibilities, change locations, and analysis findings, see [docs/AGENT.md](docs/AGENT.md).

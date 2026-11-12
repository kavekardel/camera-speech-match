# Local Sample with Aspire

By default, the Sample runs built-in library transcription and semantic matching on the host's existing Ollama server. The default local setup needs no paid AI account, API key, Docker, or gateway. Cloud and mixed setups use the same library registration. The reusable Blazor component uses `TranscribeRequest` and `MatchProbabilityRequest` for its service contracts.

## Prerequisites

- .NET 10 SDK. The AppHost pins Aspire 13.6.1; validation used SDK 10.0.400.
- Ollama with `qwen3a` audio-projector support and the `/v1/systemone` decision endpoint. Decision support requires at least 0.35; audio support is also checked through model capabilities. Ollama 0.40.4 was used for validation. An older version meeting the numeric minimum can still lack the required audio runner; use the tested version or a compatible newer release.
- These exact local models: `frozenlab/qwen3-asr:0.6b` and `nimble:9b-q4_K_M`.
- A camera, microphone, and browser supporting MediaRecorder, AudioWorklet, Web Workers, OffscreenCanvas, and WebAssembly. Allow camera/microphone access. `localhost` is a secure context; remote deployments require HTTPS.

Verify Ollama before starting:

```sh
ollama --version
ollama list
ollama show frozenlab/qwen3-asr:0.6b
ollama show nimble:9b-q4_K_M
curl http://localhost:11434/api/version
```

If a model is missing, install only the missing model explicitly:

```sh
ollama pull frozenlab/qwen3-asr:0.6b
ollama pull nimble:9b-q4_K_M
```

Startup never pulls models. Keep the desktop Ollama application running, or start `ollama serve` if there is no server already listening. Do not start a second server on the same port.

## Run and stop

From the repository root:

```sh
dotnet run --project src/CameraSpeechMatch.AppHost/CameraSpeechMatch.AppHost.csproj
```

Open the dashboard at `http://localhost:15295`; no login token is required. The local AppHost launch profile sets `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` and keeps the dashboard bound to localhost. Open the `sample` resource's HTTP endpoint from the dashboard. Aspire allocates its proxied port; direct execution uses `http://localhost:5295`:

```sh
dotnet run --project src/CameraSpeechMatch.Sample --launch-profile http
```

Choose a prompt and match mode, start recording, read the prompt with your face visible, then stop. Both English and Persian use the ASR model's automatic language detection; no language setting is required. The default Sample recording limit is 30 seconds.

Press Ctrl+C in the AppHost terminal to stop its managed Sample process and dashboard. Your existing Ollama server and installed models remain available. A separately started Sample or `ollama serve` is stopped in its own terminal.

## Configuration and readiness

AI implementations now live in the component library. The Sample calls `AddCameraSpeechMatchAI` and binds `CameraSpeechMatch:AI`; it contains no AI service implementations. See the [provider configuration examples](../../README.md#built-in-provider-configuration) for local, cloud, and mixed setups.

The two operation sections are independent:

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
        "Model": "nimble:9b-q4_K_M",
        "MinimumDecisionProbability": 0.8
      }
    }
  }
}
```

Aspire supplies `services:ollama:http:0` (or HTTPS equivalent). The Sample's registration action applies that address only to operations selecting Ollama. Direct execution uses the operation's configured endpoint. AppHost still configures its external resource through `Ollama:BaseUrl`; the Sample uses `CameraSpeechMatch:AI:Transcription:Endpoint` and `CameraSpeechMatch:AI:SemanticMatching:Endpoint`.

For cloud use, set either operation's provider to `OpenAICompatible`, choose its model, set the API base URL including `/v1`, and supply its key via user secrets or environment variables. For example:

```powershell
$env:CameraSpeechMatch__AI__SemanticMatching__Provider = 'OpenAICompatible'
$env:CameraSpeechMatch__AI__SemanticMatching__Endpoint = 'https://your-provider.example/v1'
$env:CameraSpeechMatch__AI__SemanticMatching__Model = 'your-structured-output-model'
# Supply CameraSpeechMatch__AI__SemanticMatching__ApiKey through your secret store.
dotnet run --project src/CameraSpeechMatch.Sample
```

This keeps local Ollama transcription with cloud semantic matching. Configure both operations as OpenAI-compatible for an entirely cloud setup; transcription requires the audio transcription API and matching requires strict JSON Schema Chat Completions. There is no Azure-specific credential/deployment adapter in v1. Official OpenAI requires a key; explicitly configured unauthenticated self-hosted endpoints can omit it. Credentials stay on the server.

For an entirely cloud setup, run the Sample directly; the default AppHost still declares and monitors an external Ollama resource.

The recorder waits only for the providers required by its selected mode. The provider-neutral `CameraSpeechMatchAiReadiness` updates the UI. `/alive` reports process liveness; `/health` reports the `camera-speech-match-ai` check, returning 503 until transcription and all configured matching providers are ready. Local startup checks only each operation's selected model and capability; cloud startup validates configuration without inference. Probes never pull or load models. Restart after repairing a dependency if its startup deadline expired.

Defaults per operation: `TimeoutSeconds=120`, `MaximumResponseBytes=262144`, `StartupTimeoutSeconds=60`, `ProbeTimeoutSeconds=5`, and `RetryDelaySeconds=2`. Transcription adds `MaximumAudioBytes=10485760`; matching adds `MaximumMatchingRequestBytes=65536` and native System One providers' `MinimumDecisionProbability=0.8`. Environment variables follow the operation path, for example `CameraSpeechMatch__AI__Transcription__TimeoutSeconds=180`. Both providers default to `None` in the library; the Sample explicitly selects Ollama.

### Hosted providers

The library also supplies `Jev` and `CloudflareClef` semantic matching, plus `Deepgram` and `ElevenLabs` transcription. The [hosted provider examples](../../README.md#jev-cloudflare-clef-deepgram-and-elevenlabs) cover both Jev/ElevenLabs and Jev/Deepgram setups.

For example, use this `CameraSpeechMatch:AI` configuration for Jev with automatic-language ElevenLabs transcription:

```json
{
  "Transcription": { "Provider": "ElevenLabs", "Model": "scribe_v2" },
  "SemanticMatching": { "Provider": "Jev", "Model": "jev-1.14.0" }
}
```

Supply separate keys at `CameraSpeechMatch:AI:Transcription:ApiKey` and `CameraSpeechMatch:AI:SemanticMatching:ApiKey` using your secret store. For Persian Deepgram transcription, select `Provider=Deepgram`, `Model=nova-3`, and `Language=fa`. English uses `Language=en`. Omit `Language` for ElevenLabs automatic detection.

Cloudflare uses `Provider=CloudflareClef`, `Model=clef` or `clef-flash`, the Workers AI token in `ApiKey`, and your `AccountId`. Both hosted matchers use typed Noul probabilities and the configured `MinimumDecisionProbability`. Cloudflare also applies a conservative 16 KiB request ceiling. The [Cloudflare REST setup](https://developers.cloudflare.com/workers-ai/get-started/rest-api/) describes account IDs and token permissions.

Run the Sample directly for hosted setups. The supplied AppHost declares the local Ollama resource.

## Model protocols and audio

The browser already captures raw signed PCM16 little-endian, mono, 16 kHz, independently of WebM/MP4 video. `OllamaSttService` wraps PCM in a WAV header in memory and posts `/api/chat` with `stream: false`, an empty user `content`, and base64 WAV in `messages[0].images`. It requires a completed `stop` response and extracts the transcript after `<asr_text>`. This follows the [ASR model card](https://ollama.com/frozenlab/qwen3-asr). No FFmpeg, executable, resampling, video decoding, or temporary audio files are required on any supported platform. Longer component recordings are split into consecutive 30-second windows; a failed or empty window fails the entire transcription. Window boundaries can reduce recognition accuracy, so use short prompts.

`MatchProbabilityRequest` represents a noul decision with `State`, `Instructions`, and optional string properties `TrueCriteria`/`FalseCriteria`. State and instructions can be text, objects, or arrays. The component supplies structured phrase data (`{ expectedText, transcript }`), matching instructions, and both criterion descriptions. `OllamaSystemOneService` preserves those values and maps the typed criteria to `criteria.true`/`criteria.false` when posting `/v1/systemone`, wrapping the instructions and optional criteria in a named `match` question with type `noul`. Null criteria are omitted, leaving the model's defaults for those outcomes. The adapter adds no phrase-specific guidance. It reads only `answers.match.noul`, validates its type/range, and returns the existing 0–1 probability contract. It rejects malformed and uncertain results instead of generating or interpreting unrestricted chat text. See the [API reference](https://docs.ollama.com/api/systemone) and [Nimble documentation](https://ollama.com/library/nimble). A model probability is not calibrated verification accuracy; evaluate the threshold on your own phrases.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Cannot connect to Ollama | Check `curl http://localhost:11434/api/version`, the Ollama process, the configured address, and local firewall. An existing host process uses host loopback. |
| Missing model | `/health`, UI, and logs name the missing model and its exact `ollama pull` command. Finish installation and restart the Sample/AppHost if the readiness budget expired. No fallback model is selected. |
| Unsupported audio or decision capability | Update Ollama and inspect `ollama show` for the exact tag. A text-only chat request does not validate ASR. |
| HTTP 500 during ASR or decision | Inspect Ollama logs for runner/projector errors or memory exhaustion. Capability metadata is a startup check; actual inference is the definitive compatibility test. |
| Empty/malformed transcription | Check microphone input and browser PCM capture. The adapter accepts raw PCM from the component, not an uploaded WAV/WebM/MP3. Missing ASR markers, truncated responses, and empty windows are rejected. |
| Timeout on first request | Model loading can be slow on CPU or limited RAM. Check Ollama logs and increase the relevant deadline within its configured bounds. |
| Uncertain semantic decision | Record again with a complete, clear phrase. Uncertainty fails processing even if the component minimum was lowered. |
| Readiness timed out | `/health` includes the last failure. Fix the server/model issue and restart; startup retries do not continue indefinitely. |
| Port or DLL already in use | Stop the earlier Sample before rebuilding the same configuration. Use another configuration/port for independent validation. |

## Tests

Unit/bUnit tests do not require Ollama:

```sh
dotnet build CameraSpeechMatch.slnx --configuration Release
dotnet test CameraSpeechMatch.slnx --configuration Release
```

Live integration tests are explicitly opt-in and never install models. PowerShell example:

The separate Aspire integration test starts the real AppHost/Sample against an ephemeral local HTTP double. It checks process startup, service reference propagation, readiness, liveness, and the Sample page without requiring Ollama or Docker:

```powershell
$env:ASPIRE_INTEGRATION = '1'
dotnet test tests/CameraSpeechMatch.AppHost.Tests --configuration Release
```

For actual model inference:

```powershell
$env:OLLAMA_INTEGRATION = '1'
# Optional, defaults to http://localhost:11434:
$env:OLLAMA_TEST_URL = 'http://localhost:11434'
# Enable the complete audio -> transcription -> semantic decision test:
$env:OLLAMA_TEST_WAV = 'C:\recordings\verification.wav'
$env:OLLAMA_TEST_EXPECTED = 'The exact words spoken in the recording.'
dotnet test tests/CameraSpeechMatch.Tests --filter Category=OllamaIntegration
```

Supply a RIFF WAV containing mono 16 kHz PCM16 audio; only the test fixture reader consumes WAV files. The production adapter receives raw PCM from the component. On macOS/Linux set the same variables using `export`. The connectivity/model and semantic tests need only `OLLAMA_INTEGRATION=1`; the audio test additionally needs both fixture variables. Webcam permissions, real face detection, and recognition quality in your environment require a browser/device check.

### Opt-in cloud inference

Normal tests use local HTTP doubles with the real SDK and do not need keys or models. To exercise a real OpenAI-compatible service, explicitly opt in and configure a mono 16 kHz PCM16 RIFF WAV fixture:

```powershell
$env:CSM_CLOUD_INTEGRATION = '1'
$env:CSM_TEST_AI_ENDPOINT = 'https://your-provider.example/v1'
$env:CSM_TEST_STT_MODEL = 'your-transcription-model'
$env:CSM_TEST_MATCHING_MODEL = 'your-structured-output-model'
$env:CSM_TEST_WAV = 'C:\recordings\verification.wav'
$env:CSM_TEST_EXPECTED = 'The exact words spoken in the recording.'
# Supply CSM_TEST_AI_KEY through your secret store if the endpoint requires authentication.
dotnet test tests/CameraSpeechMatch.Tests --filter Category=CloudIntegration
```

This test performs real transcription and matching calls and may incur provider charges. Chat scores are model-generated estimates, distinct from System One probabilities; evaluate thresholds on your own phrases and selected model.

### Opt-in hosted provider validation

The `HostedIntegration` tests exercise Jev, Cloudflare Clef, Deepgram, and ElevenLabs. Enable them explicitly:

```powershell
$env:CSM_HOSTED_INTEGRATION = '1'
# Set the keys for the providers you want to test through your secret store:
# CSM_JEV_API_KEY, CSM_CLOUDFLARE_API_KEY, CSM_DEEPGRAM_API_KEY, CSM_ELEVENLABS_API_KEY
# Cloudflare additionally requires CSM_CLOUDFLARE_ACCOUNT_ID.

$env:CSM_TEST_WAV_EN = 'C:\recordings\english.wav'
$env:CSM_TEST_EXPECTED_EN = 'The exact English words spoken in the recording.'
$env:CSM_TEST_WAV_FA = 'C:\recordings\persian.wav'
# Set CSM_TEST_EXPECTED_FA to the exact Persian words in persian.wav.
dotnet test tests/CameraSpeechMatch.Tests --filter Category=HostedIntegration
```

Use RIFF WAV files containing mono 16 kHz PCM16 audio. STT tests require the key and the language-specific fixture/expected-text pair. Matching tests require the key and, for Cloudflare, account ID. Each provider's tests skip when its required setup is absent.

Model defaults in these opt-in fixtures are `jev-1.14.0`, `clef-flash`, `nova-3`, and `scribe_v2`. Override them with `CSM_JEV_MODEL`, `CSM_CLOUDFLARE_MODEL`, `CSM_DEEPGRAM_MODEL`, or `CSM_ELEVENLABS_MODEL`. Corresponding `_ENDPOINT` variables can override endpoint bases. Production configuration requires an explicit model.

The matching cases cover English and Persian paraphrases, changed amounts, and negation. Recognition checks compare the expected and transcribed wording after the component's normalization. These tests make real provider calls and may incur charges. Local HTTP-double tests verify the protocols and failure handling independently of model quality.

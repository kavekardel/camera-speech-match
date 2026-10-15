# Local Sample with Aspire

The Sample runs speech transcription and semantic matching on the host's existing Ollama server. It needs no paid AI account, API key, Docker, or additional gateway. The reusable Blazor component uses `TranscribeRequest` and `MatchProbabilityRequest` for its service contracts.

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

The AppHost models Ollama as an external host service and launches only the Blazor Server Sample. `.WithReference(ollama)` supplies the `services:ollama:http:0` endpoint (or HTTPS equivalent). The Sample resolves that reference before its own `Ollama:BaseUrl`. Configure the AppHost's [appsettings.json](../CameraSpeechMatch.AppHost/appsettings.json), or set `Ollama__BaseUrl`, to change the server address. Direct Sample execution uses its [appsettings.json](appsettings.json). No secrets are needed.

Both managed processes run directly on the host, so `http://localhost:11434` reaches the existing local Ollama and model store. Docker is unnecessary. If you later containerize the Sample, configure a reachable host endpoint such as `http://host.docker.internal:11434` on Docker Desktop; `localhost` in that container refers to the container. A loopback-only Ollama listener will also need an intentionally configured private route. The supplied setup keeps Ollama on host loopback and does not change its binding or expose it publicly.

The Sample starts its web server immediately, but withholds the recorder until readiness succeeds. A background probe checks `/api/version`, exact model names in `/api/tags`, and `/api/show` capabilities (`audio` and `decision`). Each request has a deadline; retries stop after the overall startup budget. The UI updates automatically when readiness changes. `/alive` returns process liveness; `/health` returns startup readiness with diagnostic JSON and HTTP 503 until ready. Aspire monitors `/health` and separately checks the external Ollama `/api/version` endpoint. Models load on first inference; availability checks do not load or download them. Inference errors after startup fail the attempt; restart after repairing the dependency.

All settings below are under `Ollama` in the Sample. Environment variables use double underscores, for example `Ollama__MatchingTimeoutSeconds=180`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `BaseUrl` | `http://localhost:11434` | Host Ollama address; overridden by Aspire's service reference. |
| `SttModel` | `frozenlab/qwen3-asr:0.6b` | Exact ASR model. |
| `MatchingModel` | `nimble:9b-q4_K_M` | Exact decision model. |
| `StartupTimeoutSeconds` | `60` | Overall bounded readiness budget. |
| `ProbeTimeoutSeconds` | `5` | Individual readiness HTTP deadline. |
| `RetryDelaySeconds` | `2` | Delay between startup attempts. |
| `TranscriptionTimeoutSeconds` | `120` | Total transcription deadline across all audio windows. |
| `MatchingTimeoutSeconds` | `120` | Decision deadline, including body reading and cold loading. |
| `MaximumAudioBytes` | `10485760` | Raw PCM limit, no more than the component's 10 MiB limit. |
| `MaximumResponseBytes` | `262144` | Bounded JSON response size. |
| `MaximumMatchingRequestBytes` | `65536` | Decision request size, capped at the endpoint's 64 KiB limit. |
| `MinimumDecisionProbability` | `0.8` | Reject decisions between 0.2 and 0.8 as uncertain. The component separately applies its selected acceptance minimum. |

Model names and endpoint are configurable in one section. The Sample contains only local Ollama adapters. They use the documented ASR and Jev protocols; a different model must support the same protocol or have a corresponding adapter.

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

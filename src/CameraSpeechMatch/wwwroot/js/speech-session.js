const activeSessions = new Set();

export function createTranscriptState(existingText, selectionStart, selectionEnd) {
    const existing = existingText ?? "";
    const start = clamp(selectionStart ?? existing.length, 0, existing.length);
    const end = clamp(selectionEnd ?? start, start, existing.length);

    return {
        prefix: existing.slice(0, start),
        suffix: existing.slice(end),
        committed: "",
        interim: ""
    };
}

export function applyTranscriptToState(state, text, isFinal, maxLength = -1) {
    const normalized = (text ?? "").trim();

    if (isFinal) {
        state.interim = "";
        if (normalized.length > 0) {
            state.committed = joinText(state.committed, normalized);
        }
    } else {
        state.interim = normalized;
    }

    limitTranscriptSegment(state, isFinal ? "committed" : "interim", maxLength);

    const result = buildTranscriptResult(state);
    if (Number.isInteger(maxLength) && maxLength >= 0 && result.value.length > maxLength) {
        return {
            value: result.value.slice(0, maxLength),
            caret: Math.min(result.caret, maxLength)
        };
    }

    return result;
}

export function createStreamingTranscriptState() {
    return {
        committed: "",
        interim: ""
    };
}

export function applyStreamingTranscript(state, text, isFinal) {
    const fragment = (text ?? "").trim();

    if (isFinal) {
        state.interim = "";
        if (fragment.length > 0) {
            state.committed = joinText(state.committed, fragment);
        }
    } else {
        state.interim = fragment;
    }

    return {
        committed: state.committed,
        interim: state.interim,
        display: joinText(state.committed, state.interim)
    };
}

export function normalizeSpeechLanguage(language, fallback = "fa") {
    if (typeof language !== "string" || language.trim().length === 0) {
        return fallback;
    }

    const primaryLanguage = language.trim().toLowerCase().split(/[-_]/u, 1)[0];
    return primaryLanguage === "fa" || primaryLanguage === "en"
        ? primaryLanguage
        : null;
}

export function claimCompletion(state) {
    if (state.completionClaimed) {
        return false;
    }

    state.completionClaimed = true;
    return true;
}

export function isStopShortcut(event) {
    return event?.key === "Escape" && !event.repeat;
}

export function createSpeechSession(options) {
    return {
        options: {
            language: options?.language,
            stream: options?.stream ?? null,
            mediaConstraints: options?.mediaConstraints ?? {
                audio: {
                    channelCount: 1,
                    echoCancellation: true,
                    noiseSuppression: true,
                    autoGainControl: true
                },
                video: false
            },
            stopTracksOnRelease: options?.stopTracksOnRelease !== false,
            requireGatewayCompletion: options?.requireGatewayCompletion === true,
            relayUrl: options?.relayUrl ?? null,
            readyTimeoutMilliseconds: options?.readyTimeoutMilliseconds ?? 15_000,
            completionTimeoutMilliseconds: options?.completionTimeoutMilliseconds ?? 6_000,
            onStateChanged: options?.onStateChanged,
            onTranscript: options?.onTranscript,
            onTiming: options?.onTiming,
            logName: options?.logName ?? "SpeechInput"
        },
        socket: null,
        stream: options?.stream ?? null,
        audioContext: null,
        sourceNode: null,
        workletNode: null,
        silentGain: null,
        stopping: false,
        finished: false,
        started: false,
        providerReady: false,
        gatewayCompleted: false,
        uiState: "idle",
        finalState: null,
        errorCode: null,
        readyAt: null,
        audioStartedAt: null,
        activityTimer: null,
        lastVoiceAt: null,
        lastTranscriptAt: null,
        pendingSpeech: false,
        firstTranscriptAt: null,
        firstFinalAt: null,
        startedAt: performance.now(),
        pendingAudio: [],
        pendingAudioBytes: 0,
        readyPromise: null,
        readyResolve: null,
        readyReject: null,
        completionPromise: null,
        completedResolve: null
    };
}

export async function startSpeechSession(session) {
    if (session.started || session.finished) {
        return getSpeechSessionOutcome(session);
    }

    session.started = true;
    activeSessions.add(session);
    session.uiState = "connecting";

    if (!isBrowserSupported()) {
        await failSession(session, "unsupported_browser");
        return getSpeechSessionOutcome(session);
    }

    const language = normalizeSpeechLanguage(session.options.language, "");
    if (!language) {
        await failSession(session, "unsupported_language");
        return getSpeechSessionOutcome(session);
    }
    session.options.language = language;

    try {
        session.readyPromise = new Promise((resolve, reject) => {
            session.readyResolve = resolve;
            session.readyReject = reject;
        });
        session.socket = createRelaySocket(session);

        if (session.finished || session.stopping) {
            return getSpeechSessionOutcome(session);
        }

        if (!session.stream) {
            session.stream = await navigator.mediaDevices.getUserMedia(session.options.mediaConstraints);
        }

        if (session.finished || session.stopping) {
            await releaseAudio(session);
            return getSpeechSessionOutcome(session);
        }

        if (!session.stream.getAudioTracks().some(track => track.readyState === "live")) {
            throw new SpeechSessionError("microphone_unavailable");
        }

        const audioStarted = await startAudio(session);
        if (!audioStarted || session.finished || session.stopping) {
            return getSpeechSessionOutcome(session);
        }

        await withTimeout(
            session.readyPromise,
            session.options.readyTimeoutMilliseconds,
            "gateway_timeout");
    } catch (error) {
        if (!session.finished && !session.stopping) {
            await failSession(session, mapBrowserError(error));
        }
    }

    return getSpeechSessionOutcome(session);
}

export async function stopSpeechSession(session) {
    if (session.finished) {
        return getSpeechSessionOutcome(session);
    }

    if (session.stopping) {
        await session.completionPromise;
        return getSpeechSessionOutcome(session);
    }

    session.stopping = true;
    session.completionPromise = new Promise(resolve => {
        session.completedResolve = resolve;
    });

    await emitState(session, "stopping");
    await releaseAudio(session);

    if (session.socket?.readyState === WebSocket.OPEN) {
        sendComplete(session.socket);
    } else if (session.socket?.readyState !== WebSocket.CONNECTING) {
        if (session.options.requireGatewayCompletion) {
            await failSession(session, "gateway_unavailable");
        } else {
            await finishSession(session, "idle");
        }
        return getSpeechSessionOutcome(session);
    }

    try {
        await withTimeout(
            session.completionPromise,
            session.options.completionTimeoutMilliseconds,
            "completion_timeout");
    } catch {
        if (session.options.requireGatewayCompletion) {
            await failSession(session, "completion_timeout");
        } else {
            // Text fields retain their last visible interim text if finalization times out.
            await finishSession(session, "idle");
        }
    }

    return getSpeechSessionOutcome(session);
}

export async function disposeSpeechSession(session) {
    if (session.finished) {
        return;
    }

    session.stopping = true;
    if (session.socket?.readyState === WebSocket.OPEN) {
        sendComplete(session.socket);
    }
    await finishSession(session, "idle", false);
}

export function getSpeechSessionOutcome(session) {
    return {
        succeeded: session.finished && session.finalState === "idle" && !session.errorCode,
        finished: session.finished,
        gatewayCompleted: session.gatewayCompleted,
        errorCode: session.errorCode
    };
}

export function calculateRms(input) {
    if (!input?.length) {
        return 0;
    }

    let sumOfSquares = 0;
    for (let index = 0; index < input.length; index++) {
        sumOfSquares += input[index] * input[index];
    }
    return Math.sqrt(sumOfSquares / input.length);
}

export function downsampleToPcm16(input, inputRate, outputRate) {
    const ratio = inputRate / outputRate;
    const outputLength = Math.floor(input.length / ratio);
    const output = new ArrayBuffer(outputLength * 2);
    const view = new DataView(output);

    for (let outputIndex = 0; outputIndex < outputLength; outputIndex++) {
        const start = Math.floor(outputIndex * ratio);
        const end = Math.max(start + 1, Math.floor((outputIndex + 1) * ratio));
        let sum = 0;
        let count = 0;

        for (let inputIndex = start; inputIndex < end && inputIndex < input.length; inputIndex++) {
            sum += input[inputIndex];
            count++;
        }

        const sample = Math.max(-1, Math.min(1, count > 0 ? sum / count : 0));
        view.setInt16(outputIndex * 2, sample < 0 ? sample * 0x8000 : sample * 0x7fff, true);
    }

    return output;
}

function resolveRelayUrl(relayUrl) {
    if (typeof relayUrl === "string" && relayUrl.trim().length > 0) {
        const trimmed = relayUrl.trim();
        if (/^wss?:\/\//i.test(trimmed)) {
            return trimmed;
        }
        if (/^https?:\/\//i.test(trimmed)) {
            return trimmed.replace(/^http/i, "ws");
        }
        const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
        const path = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
        return `${scheme}//${window.location.host}${path}`;
    }

    const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
    return `${scheme}//${window.location.host}/api/stt/v1/relay`;
}

function createRelaySocket(session) {
    const socket = new WebSocket(resolveRelayUrl(session.options.relayUrl));
    socket.binaryType = "arraybuffer";

    socket.onopen = () => {
        if (session.finished) {
            socket.close(1000, "Speech session ended");
            return;
        }

        socket.send(JSON.stringify({
            language: session.options.language,
            audioFormat: "pcm_s16le",
            sampleRate: 16000,
            channels: 1
        }));

        for (const audio of session.pendingAudio) {
            socket.send(audio);
        }
        session.pendingAudio = [];
        session.pendingAudioBytes = 0;

        if (session.stopping) {
            sendComplete(socket);
        }
    };

    socket.onmessage = async event => {
        if (typeof event.data !== "string" || session.finished) {
            return;
        }

        let message;
        try {
            message = JSON.parse(event.data);
        } catch {
            await failSession(session, "invalid_response");
            return;
        }

        switch (message.type) {
            case "ready":
                session.readyAt = performance.now();
                session.providerReady = true;
                console.debug(`[${session.options.logName}] provider connection ready`, {
                    providerConnectionLatencyMs: message.connectionLatencyMs,
                    browserToReadyLatencyMs: Math.round(session.readyAt - session.startedAt)
                });
                await emitState(
                    session,
                    "listening",
                    null,
                    Math.round(session.readyAt - session.startedAt));
                refreshActivityState(session);
                session.readyResolve?.(message);
                break;
            case "transcript":
                await handleTranscript(
                    session,
                    message.text,
                    message.isFinal === true,
                    message.utteranceFinal === true);
                break;
            case "error":
                session.readyReject?.(new SpeechSessionError(message.code ?? "gateway_error"));
                await failSession(session, message.code ?? "gateway_error");
                break;
            case "completed":
                session.gatewayCompleted = true;
                await finishSession(session, "idle");
                break;
        }
    };

    socket.onerror = () => {
        session.readyReject?.(new SpeechSessionError("gateway_unavailable"));
    };

    socket.onclose = async () => {
        if (session.finished) {
            return;
        }

        if (session.stopping && !session.options.requireGatewayCompletion) {
            await finishSession(session, "idle");
        } else {
            session.readyReject?.(new SpeechSessionError("gateway_unavailable"));
            await failSession(session, "gateway_unavailable");
        }
    };

    return socket;
}

async function startAudio(session) {
    session.audioContext = new AudioContext({ latencyHint: "interactive" });
    await session.audioContext.audioWorklet.addModule(new URL("./speech-input-worklet.js", import.meta.url));

    if (session.finished || session.stopping) {
        await releaseAudio(session);
        return false;
    }

    session.sourceNode = session.audioContext.createMediaStreamSource(session.stream);
    session.workletNode = new AudioWorkletNode(session.audioContext, "stt-pcm-capture");
    session.silentGain = session.audioContext.createGain();
    session.silentGain.gain.value = 0;

    session.workletNode.port.onmessage = event => {
        if (session.stopping || session.finished) {
            return;
        }

        const now = performance.now();
        if (calculateRms(event.data) >= 0.015) {
            session.lastVoiceAt = now;
            session.pendingSpeech = true;
        }

        const pcm = downsampleToPcm16(event.data, session.audioContext.sampleRate, 16000);
        if (pcm.byteLength > 0) {
            sendOrQueueAudio(session, pcm);
        }
    };

    session.sourceNode.connect(session.workletNode);
    session.workletNode.connect(session.silentGain);
    session.silentGain.connect(session.audioContext.destination);
    await session.audioContext.resume();

    if (session.finished || session.stopping || !session.audioContext) {
        await releaseAudio(session);
        return false;
    }

    session.audioStartedAt = performance.now();
    session.activityTimer = window.setInterval(() => refreshActivityState(session), 250);
    return true;
}

async function handleTranscript(session, text, isFinal, utteranceFinal) {
    const now = performance.now();
    session.lastTranscriptAt = now;
    if (utteranceFinal) {
        session.pendingSpeech = false;
    }
    refreshActivityState(session);

    await callSafely(session.options.onTranscript, text ?? "", isFinal, utteranceFinal);

    if (!session.firstTranscriptAt) {
        session.firstTranscriptAt = now;
        const latencyMs = session.audioStartedAt ? Math.round(now - session.audioStartedAt) : 0;
        console.debug(`[${session.options.logName}] first visible transcript`, { latencyMs });
        await callSafely(session.options.onTiming, "first-visible-transcript", latencyMs);
    }
    if (isFinal && !session.firstFinalAt) {
        session.firstFinalAt = now;
        const latencyMs = session.audioStartedAt ? Math.round(now - session.audioStartedAt) : 0;
        console.debug(`[${session.options.logName}] first final transcript`, { latencyMs });
        await callSafely(session.options.onTiming, "first-final-transcript", latencyMs);
    }
}

function refreshActivityState(session) {
    if (!session.providerReady || session.stopping || session.finished) {
        return;
    }

    const now = performance.now();
    const recognitionReference = session.lastTranscriptAt ?? session.audioStartedAt ?? now;
    const transcriptIsBehind = session.pendingSpeech && now - recognitionReference >= 1200;
    const waitingAfterSpeech = session.pendingSpeech &&
        session.lastVoiceAt !== null &&
        now - session.lastVoiceAt >= 500;
    const desiredState = transcriptIsBehind || waitingAfterSpeech ? "processing" : "listening";

    if (desiredState !== session.uiState) {
        void emitState(session, desiredState);
    }
}

function sendOrQueueAudio(session, audio) {
    if (session.socket?.readyState === WebSocket.OPEN) {
        session.socket.send(audio);
        return;
    }

    // Retain at most five seconds while the same-origin socket opens.
    const maximumPendingBytes = 16000 * 2 * 5;
    if (session.pendingAudioBytes + audio.byteLength <= maximumPendingBytes) {
        session.pendingAudio.push(audio);
        session.pendingAudioBytes += audio.byteLength;
    }
}

async function releaseAudio(session) {
    if (session.activityTimer !== null) {
        window.clearInterval(session.activityTimer);
        session.activityTimer = null;
    }

    if (session.workletNode) {
        session.workletNode.port.onmessage = null;
        session.workletNode.disconnect();
        session.workletNode = null;
    }
    session.sourceNode?.disconnect();
    session.sourceNode = null;
    session.silentGain?.disconnect();
    session.silentGain = null;

    if (session.options.stopTracksOnRelease) {
        for (const track of session.stream?.getTracks() ?? []) {
            track.stop();
        }
    }
    session.stream = null;

    if (session.audioContext && session.audioContext.state !== "closed") {
        try {
            await session.audioContext.close();
        } catch {
        }
    }
    session.audioContext = null;
}

async function failSession(session, errorCode) {
    if (session.finished) {
        return;
    }

    session.errorCode = errorCode;
    await finishSession(session, "error", true, errorCode);
}

async function finishSession(session, finalState, notifyComponent = true, errorCode = null) {
    if (session.finished) {
        return;
    }

    session.finished = true;
    session.finalState = finalState;
    session.errorCode = errorCode;
    await releaseAudio(session);

    if (session.socket && session.socket.readyState < WebSocket.CLOSING) {
        session.socket.close(1000, "Speech session ended");
    }

    activeSessions.delete(session);
    session.readyReject?.(new SpeechSessionError("session_stopped"));
    if (notifyComponent) {
        await emitState(session, finalState, errorCode);
    }
    session.completedResolve?.();
}

async function emitState(session, state, errorCode = null, latencyMs = null) {
    session.uiState = state;
    await callSafely(session.options.onStateChanged, state, errorCode, latencyMs);
}

async function callSafely(callback, ...args) {
    if (typeof callback !== "function") {
        return;
    }

    try {
        await callback(...args);
    } catch {
        // A Blazor circuit or owning component may have been disposed.
    }
}

function sendComplete(socket) {
    try {
        socket.send(JSON.stringify({ type: "complete" }));
    } catch {
    }
}

function buildTranscriptResult(state, segmentName = null, segmentValue = null) {
    const committed = segmentName === "committed" ? segmentValue : state.committed;
    const interim = segmentName === "interim" ? segmentValue : state.interim;
    const dictated = joinText(committed, interim);
    const beforeCaret = joinText(state.prefix, dictated);
    return {
        value: joinText(beforeCaret, state.suffix),
        caret: beforeCaret.length
    };
}

function limitTranscriptSegment(state, segmentName, maxLength) {
    if (!Number.isInteger(maxLength) || maxLength < 0) {
        return;
    }

    const segment = state[segmentName];
    if (buildTranscriptResult(state).value.length <= maxLength) {
        return;
    }

    let minimum = 0;
    let maximum = segment.length;
    while (minimum < maximum) {
        const candidateLength = Math.ceil((minimum + maximum) / 2);
        const candidate = segment.slice(0, candidateLength).trimEnd();
        if (buildTranscriptResult(state, segmentName, candidate).value.length <= maxLength) {
            minimum = candidateLength;
        } else {
            maximum = candidateLength - 1;
        }
    }

    state[segmentName] = segment.slice(0, minimum).trimEnd();
}

function joinText(left, right) {
    if (!left) {
        return right ?? "";
    }
    if (!right) {
        return left;
    }
    if (/\s$/u.test(left) || /^\s/u.test(right) || /^[.,!?:;،؛؟]/u.test(right)) {
        return left + right;
    }
    return `${left} ${right}`;
}

function clamp(value, minimum, maximum) {
    return Math.min(Math.max(value, minimum), maximum);
}

function withTimeout(promise, milliseconds, code) {
    return Promise.race([
        promise,
        new Promise((_, reject) => setTimeout(() => reject(new SpeechSessionError(code)), milliseconds))
    ]);
}

function isBrowserSupported() {
    return window.isSecureContext &&
        navigator.mediaDevices?.getUserMedia &&
        window.AudioContext &&
        window.AudioWorkletNode &&
        window.WebSocket;
}

function mapBrowserError(error) {
    if (error instanceof SpeechSessionError) {
        return error.code;
    }
    if (error?.name === "NotAllowedError" || error?.name === "SecurityError") {
        return "permission_denied";
    }
    if (error?.name === "NotFoundError" || error?.name === "NotReadableError") {
        return "microphone_unavailable";
    }
    return "browser_error";
}

function abortSessionImmediately(session) {
    if (session.finished) {
        return;
    }

    session.finished = true;
    session.stopping = true;
    session.workletNode?.disconnect();
    session.sourceNode?.disconnect();
    session.silentGain?.disconnect();
    for (const track of session.stream?.getTracks() ?? []) {
        track.stop();
    }
    if (session.socket?.readyState === WebSocket.OPEN) {
        sendComplete(session.socket);
        session.socket.close(1000, "Page navigation");
    }
    void session.audioContext?.close();
    activeSessions.delete(session);
}

class SpeechSessionError extends Error {
    constructor(code) {
        super(code);
        this.code = code;
    }
}

if (typeof window !== "undefined") {
    window.addEventListener("pagehide", () => {
        for (const session of [...activeSessions]) {
            abortSessionImmediately(session);
        }
    });
}

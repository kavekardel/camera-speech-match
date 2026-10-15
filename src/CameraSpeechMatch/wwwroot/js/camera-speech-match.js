import {
    applyStreamingTranscript,
    claimCompletion,
    createSpeechSession,
    createStreamingTranscriptState,
    disposeSpeechSession,
    getSpeechSessionOutcome,
    startSpeechSession,
    stopSpeechSession
} from "./speech-session.js";
import { createFaceTracker } from "./camera-speech-face.js";
import { createPcmRecording } from "./pcm-recording.js";

const sessions = new WeakMap();
const activeSessions = new Set();

const supportedMimeTypes = [
    "video/webm;codecs=vp9,opus",
    "video/webm;codecs=vp8,opus",
    "video/webm",
    "video/mp4;codecs=avc1.42E01E,mp4a.40.2",
    "video/mp4"
];

export function initialize(videoElement, dotNetReference) {
    if (!videoElement) {
        throw new Error("The camera preview element is unavailable.");
    }

    const previousSession = sessions.get(videoElement);
    if (previousSession) {
        void disposeSession(previousSession);
    }

    const session = createSession(videoElement, dotNetReference);
    sessions.set(videoElement, session);
    activeSessions.add(session);
}

export async function startCamera(videoElement, faceOptions) {
    const session = getSession(videoElement);

    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
        throw new Error("Camera and microphone access is not supported in this browser or secure context.");
    }

    await resetSession(session);
    session.completionClaimed = false;
    const attemptId = session.attemptId;

    try {
        const stream = await navigator.mediaDevices.getUserMedia({
            video: { facingMode: "user" },
            audio: true
        });

        if (!isCurrentAttempt(session, attemptId)) {
            stream.getTracks().forEach(track => track.stop());
            return;
        }

        session.stream = stream;
        session.videoElement.srcObject = stream;
        session.videoElement.muted = true;
        session.videoElement.playsInline = true;
        await session.videoElement.play();
        if (!isCurrentAttempt(session, attemptId)) {
            return;
        }
        session.faceTracker = createFaceTracker(session.videoElement, faceOptions,
            (status, isFaceVisible) => {
                if (!isCurrentAttempt(session, attemptId)) {
                    return;
                }
                if (status === "unavailable" && !session.recorder && !session.finalizing) {
                    void failAttempt(session, "face_detection_unavailable", attemptId);
                }
                return invokeDotNet(session, "OnFacePositionChanged", status, isFaceVisible);
            });
        // Unsupported browsers can fail synchronously before the tracker is assigned.
        if (session.completionClaimed) {
            stopFaceTracking(session);
        }
    } catch (error) {
        if (!isCurrentAttempt(session, attemptId)) {
            return;
        }
        stopTracks(session);
        throw new Error(toMediaAccessErrorMessage(error));
    }
}

export async function startRecording(videoElement, options) {
    const session = getSession(videoElement);
    const attemptId = session.attemptId;
    if (session.completionClaimed || session.finalizing) {
        return false;
    }
    if (session.recorder) {
        return true;
    }
    if (typeof MediaRecorder === "undefined" || typeof MediaRecorder.isTypeSupported !== "function") {
        stopTracks(session);
        throw new Error("Video recording is not supported in this browser.");
    }

    if (!hasLiveCameraAndMicrophone(session.stream)) {
        stopTracks(session);
        throw new Error("The camera or microphone is no longer available. Please retry.");
    }

    const mimeType = supportedMimeTypes.find(type => MediaRecorder.isTypeSupported(type));
    if (!mimeType) {
        stopTracks(session);
        throw new Error("This browser does not provide a supported WebM or MP4 recording format.");
    }

    const pcmRecording = createPcmRecording(session.stream,
        () => failAttempt(session, "pcm_capture_failed", attemptId));
    session.pcmRecording = pcmRecording;
    try {
        await pcmRecording.prepare();
    } catch (error) {
        if (!isCurrentAttempt(session, attemptId)) {
            return false;
        }
        stopTracks(session);
        throw error;
    }
    if (!isCurrentAttempt(session, attemptId) || session.completionClaimed || session.finalizing) {
        await pcmRecording.dispose();
        return false;
    }
    if (!hasLiveCameraAndMicrophone(session.stream)) {
        stopTracks(session);
        throw new Error("The camera or microphone is no longer available. Please retry.");
    }

    // Recheck browser-local evidence after any asynchronous audio preparation.
    // A delayed Blazor callback may refer to a face that has already disappeared.
    if (!session.faceTracker?.startRecordingValidation()) {
        await pcmRecording.dispose();
        if (session.pcmRecording === pcmRecording) {
            session.pcmRecording = null;
        }
        return false;
    }

    session.maxRecordingSeconds = clamp(options?.maxRecordingSeconds ?? 30, 1, 300);
    session.mimeType = mimeType;
    session.chunks = [];
    session.transcript = createStreamingTranscriptState();
    session.completionClaimed = false;
    session.finalizing = false;
    session.lastElapsedSecond = -1;

    try {
        session.recorder = new MediaRecorder(session.stream, { mimeType });
        session.recorder.ondataavailable = event => {
            if (isCurrentAttempt(session, attemptId) && event.data?.size > 0) {
                session.chunks.push(event.data);
            }
        };
        session.recorder.onerror = () => {
            void failAttempt(session, "recording_failure", attemptId);
        };

        const relayUrl = typeof options?.relayUrl === "string" ? options.relayUrl.trim() : "";
        session.speechSession = relayUrl.length === 0
            ? null
            : createSpeechSession({
                language: options?.language,
                stream: session.stream,
                stopTracksOnRelease: true,
                requireGatewayCompletion: true,
                relayUrl,
                onStateChanged: (state, errorCode) => {
                    if (state === "error") {
                        void failAttempt(session, errorCode ?? "gateway_error", attemptId);
                    }
                },
                onTranscript: (text, isFinal) => {
                    if (isCurrentAttempt(session, attemptId)) {
                        updateTranscript(session, text, isFinal);
                    }
                },
                logName: "CameraSpeechMatch"
            });

        session.startedAt = performance.now();
        session.recorder.start(1000);
        pcmRecording.start();
        notifyElapsedTime(session, 0);

        session.tickTimerId = window.setInterval(() => {
            const elapsedSeconds = Math.min(
                session.maxRecordingSeconds,
                Math.floor((performance.now() - session.startedAt) / 1000));
            notifyElapsedTime(session, elapsedSeconds);
        }, 250);

        session.stopTimerId = window.setTimeout(
            () => void finalizeAttempt(session),
            session.maxRecordingSeconds * 1000);

        session.startPromise = session.speechSession
            ? startSpeechSession(session.speechSession)
                .then(outcome => {
                    if (outcome.finished && !outcome.succeeded && !session.completionClaimed) {
                        return failAttempt(session, outcome.errorCode ?? "gateway_error", attemptId);
                    }
                    return undefined;
                })
                .catch(() => failAttempt(session, "browser_error", attemptId))
            : Promise.resolve();
        return true;
    } catch (error) {
        clearTimers(session);
        abortRecorderImmediately(session);
        clearRecording(session);
        stopTracks(session);
        throw new Error(error?.message || "Video recording could not be started.");
    }
}

export function stopRecording(videoElement) {
    const session = getSession(videoElement);

    if (!session.recorder || session.recorder.state === "inactive" || session.finalizing) {
        return;
    }

    void finalizeAttempt(session);
}

export async function retry(videoElement) {
    const session = getSession(videoElement);
    await resetSession(session);
}

export async function dispose(videoElement) {
    const session = sessions.get(videoElement);
    if (!session) {
        return;
    }

    await disposeSession(session);
    sessions.delete(videoElement);
    activeSessions.delete(session);
}

function createSession(videoElement, dotNetReference) {
    return {
        videoElement,
        dotNetReference,
        stream: null,
        recorder: null,
        speechSession: null,
        pcmRecording: null,
        faceTracker: null,
        transcript: createStreamingTranscriptState(),
        chunks: [],
        mimeType: "",
        previewUrl: null,
        maxRecordingSeconds: 30,
        startedAt: 0,
        lastElapsedSecond: -1,
        tickTimerId: null,
        stopTimerId: null,
        startPromise: null,
        stopPromise: null,
        finalizing: false,
        attemptId: 0,
        completionClaimed: true,
        disposed: false
    };
}

function getSession(videoElement) {
    const session = sessions.get(videoElement);
    if (!session || session.disposed) {
        throw new Error("Video verification is not initialized.");
    }

    return session;
}

function isCurrentAttempt(session, attemptId) {
    return !session.disposed && session.attemptId === attemptId;
}

function finalizeAttempt(session) {
    if (session.stopPromise) {
        return session.stopPromise;
    }

    session.finalizing = true;
    session.stopPromise = finalizeAttemptCore(session, session.attemptId);
    return session.stopPromise;
}

async function finalizeAttemptCore(session, attemptId) {
    clearTimers(session);

    try {
        const faceValidationPromise = session.faceTracker?.finishRecordingValidation() ??
            Promise.resolve({ passed: false, errorCode: "face_detection_unavailable" });
        // Stop MediaRecorder before waiting for interop or the already-pending inference.
        const videoBlobPromise = stopMediaRecorder(session);
        const pcmBlobPromise = session.pcmRecording?.stop() ?? Promise.resolve(null);
        const processingPromise = invokeDotNet(session, "OnProcessingStarted");
        const [videoBlob, faceValidation, pcmBlob] = await Promise.all([videoBlobPromise, faceValidationPromise, pcmBlobPromise]);
        if (!isCurrentAttempt(session, attemptId)) {
            return;
        }
        stopFaceTracking(session);
        await processingPromise;
        if (!isCurrentAttempt(session, attemptId) || session.completionClaimed) {
            return;
        }
        if (!faceValidation.passed) {
            await failAttempt(session, faceValidation.errorCode ?? "face_not_visible");
            return;
        }
        if (!videoBlob || videoBlob.size === 0) {
            await failAttempt(session, "empty_content");
            return;
        }
        if (!pcmBlob || pcmBlob.size === 0) {
            await failAttempt(session, "pcm_capture_failed");
            return;
        }

        if (!isCurrentAttempt(session, attemptId) || session.completionClaimed) {
            return;
        }

        if (session.speechSession) {
            const outcome = await stopSpeechSession(session.speechSession);

            if (!isCurrentAttempt(session, attemptId) || session.completionClaimed) {
                return;
            }

            if (!outcome.succeeded || !outcome.gatewayCompleted) {
                await failAttempt(session, outcome.errorCode ?? "gateway_unavailable");
                return;
            }
        }

        stopTracks(session);
        await completeAttempt(session, session.transcript.committed, videoBlob, faceValidation.passed, pcmBlob);
    } catch {
        await failAttempt(session, "recording_failure", attemptId);
    }
}

function updateTranscript(session, text, isFinal) {
    if (session.disposed || session.completionClaimed) {
        return;
    }

    const transcript = applyStreamingTranscript(session.transcript, text, isFinal);
    void invokeDotNet(
        session,
        "OnTranscriptChanged",
        transcript.committed,
        transcript.interim,
        transcript.display);
}

async function completeAttempt(session, transcript, videoBlob, faceValidationPassed, pcmBlob) {
    const attemptId = session.attemptId;
    if (!faceValidationPassed) {
        await failAttempt(session, "face_not_visible");
        return;
    }
    if (session.disposed || !claimCompletion(session)) {
        return;
    }

    clearTimers(session);
    revokePreviewUrl(session);
    session.previewUrl = URL.createObjectURL(videoBlob);

    if (typeof DotNet === "undefined" || typeof DotNet.createJSStreamReference !== "function") {
        revokePreviewUrl(session);
        await invokeDotNet(session, "OnVerificationFailed", "unsupported_browser");
        return;
    }

    const contentType = (videoBlob.type || session.mimeType).split(";", 1)[0].toLowerCase();
    const videoStreamReference = DotNet.createJSStreamReference(videoBlob);
    const pcmStreamReference = DotNet.createJSStreamReference(pcmBlob);
    clearRecording(session);
    stopTracks(session);
    showRecordedVideo(session);
    const accepted = await invokeDotNet(session, "OnTranscriptionCompletedWithPcm",
        transcript ?? "", videoStreamReference, contentType, session.previewUrl, faceValidationPassed, pcmStreamReference);

    if (isCurrentAttempt(session, attemptId) && accepted !== true) {
        clearVideoPlayback(session);
        revokePreviewUrl(session);
    }
}

async function failAttempt(session, errorCode, attemptId = session.attemptId) {
    if (!isCurrentAttempt(session, attemptId) || !claimCompletion(session)) {
        return;
    }

    stopFaceTracking(session);
    clearTimers(session);
    session.finalizing = true;
    await stopMediaRecorder(session, true);
    if (!isCurrentAttempt(session, attemptId)) {
        return;
    }

    if (session.speechSession && !getSpeechSessionOutcome(session.speechSession).finished) {
        try {
            await disposeSpeechSession(session.speechSession);
        } catch {
        }
    }

    if (!isCurrentAttempt(session, attemptId)) {
        return;
    }
    revokePreviewUrl(session);
    clearRecording(session);
    stopTracks(session);
    await invokeDotNet(session, "OnVerificationFailed", errorCode);
}

async function stopMediaRecorder(session, suppressErrors = false) {
    const attemptId = session.attemptId;
    const recorder = session.recorder;
    session.recorder = null;
    if (!recorder) {
        const existingBlob = createRecordingBlob(session);
        if (!existingBlob && !suppressErrors) {
            throw new Error("MediaRecorder is unavailable.");
        }
        return existingBlob;
    }

    try {
        if (recorder.state !== "inactive") {
            await new Promise((resolve, reject) => {
                const timeoutId = window.setTimeout(
                    () => reject(new Error("MediaRecorder did not stop.")),
                    3000);

                recorder.addEventListener("stop", () => {
                    window.clearTimeout(timeoutId);
                    resolve();
                }, { once: true });
                recorder.addEventListener("error", () => {
                    window.clearTimeout(timeoutId);
                    reject(new Error("MediaRecorder failed while stopping."));
                }, { once: true });
                recorder.stop();
            });
        }

        return isCurrentAttempt(session, attemptId) ? createRecordingBlob(session) : null;
    } catch (error) {
        if (!suppressErrors) {
            throw error;
        }
        return null;
    } finally {
        recorder.ondataavailable = null;
        recorder.onerror = null;
        if (suppressErrors && isCurrentAttempt(session, attemptId)) {
            clearRecording(session);
        }
    }
}

function createRecordingBlob(session) {
    return buildRecordingBlob(session.chunks, session.mimeType);
}

export function buildRecordingBlob(chunks, mimeType) {
    if (!Array.isArray(chunks) || chunks.length === 0) {
        return null;
    }

    return new Blob(chunks, { type: mimeType });
}

function notifyElapsedTime(session, elapsedSeconds) {
    if (session.disposed || elapsedSeconds === session.lastElapsedSecond) {
        return;
    }

    session.lastElapsedSecond = elapsedSeconds;
    void invokeDotNet(session, "OnRecordingTick", elapsedSeconds);
}

async function invokeDotNet(session, methodName, ...args) {
    if (session.disposed || !session.dotNetReference) {
        return null;
    }

    try {
        return await session.dotNetReference.invokeMethodAsync(methodName, ...args);
    } catch {
        // The Blazor circuit or component may have been disposed while recording.
        return null;
    }
}

async function resetSession(session) {
    session.attemptId++;
    stopFaceTracking(session);
    session.completionClaimed = true;
    session.finalizing = true;
    clearTimers(session);
    revokePreviewUrl(session);
    await stopMediaRecorder(session, true);

    if (session.speechSession && !getSpeechSessionOutcome(session.speechSession).finished) {
        try {
            await disposeSpeechSession(session.speechSession);
        } catch {
        }
    }

    clearRecording(session);
    stopTracks(session);
    clearVideoPlayback(session);
    session.speechSession = null;
    session.transcript = createStreamingTranscriptState();
    session.startPromise = null;
    session.stopPromise = null;
    session.finalizing = false;
    session.lastElapsedSecond = -1;
}

async function disposeSession(session) {
    if (session.disposed) {
        return;
    }

    await resetSession(session);
    session.disposed = true;
    session.dotNetReference = null;
}

function clearRecording(session) {
    session.chunks = [];
    session.mimeType = "";
}

function revokePreviewUrl(session) {
    if (!session.previewUrl) {
        return;
    }

    URL.revokeObjectURL(session.previewUrl);
    session.previewUrl = null;
}

function abortRecorderImmediately(session) {
    const recorder = session.recorder;
    session.recorder = null;
    if (!recorder) {
        return;
    }

    recorder.ondataavailable = null;
    recorder.onerror = null;
    if (recorder.state !== "inactive") {
        try {
            recorder.stop();
        } catch {
        }
    }
}

function stopTracks(session) {
    stopFaceTracking(session);
    void session.pcmRecording?.dispose();
    session.pcmRecording = null;
    for (const track of session.stream?.getTracks() ?? []) {
        track.stop();
    }
    session.stream = null;

    if (session.videoElement) {
        session.videoElement.srcObject = null;
    }
}

function stopFaceTracking(session) {
    session.faceTracker?.stop();
    session.faceTracker = null;
}

function showRecordedVideo(session) {
    if (!session.videoElement || !session.previewUrl) {
        return;
    }

    session.videoElement.srcObject = null;
    session.videoElement.src = session.previewUrl;
    session.videoElement.autoplay = false;
    session.videoElement.muted = false;
    session.videoElement.controls = true;
    session.videoElement.currentTime = 0;
    session.videoElement.load();
}

function clearVideoPlayback(session) {
    if (!session.videoElement) {
        return;
    }

    session.videoElement.controls = false;
    session.videoElement.removeAttribute("src");
    session.videoElement.load();
}

function clearTimers(session) {
    if (session.tickTimerId !== null) {
        window.clearInterval(session.tickTimerId);
        session.tickTimerId = null;
    }

    if (session.stopTimerId !== null) {
        window.clearTimeout(session.stopTimerId);
        session.stopTimerId = null;
    }
}

function hasLiveCameraAndMicrophone(stream) {
    return stream &&
        stream.getVideoTracks().some(track => track.readyState === "live") &&
        stream.getAudioTracks().some(track => track.readyState === "live");
}

function toMediaAccessErrorMessage(error) {
    switch (error?.name) {
        case "NotAllowedError":
        case "SecurityError":
            return "Camera or microphone permission was denied.";
        case "NotFoundError":
        case "DevicesNotFoundError":
            return "A camera or microphone is not available on this device.";
        case "NotReadableError":
        case "TrackStartError":
            return "The camera or microphone is unavailable or already in use.";
        case "OverconstrainedError":
        case "ConstraintNotSatisfiedError":
            return "The camera cannot satisfy the requested recording settings.";
        default:
            return error?.message || "The camera and microphone could not be started.";
    }
}

function clamp(value, minimum, maximum) {
    return Math.min(maximum, Math.max(minimum, Number(value) || minimum));
}

function abortImmediately(session) {
    session.attemptId++;
    session.completionClaimed = true;
    clearTimers(session);
    abortRecorderImmediately(session);
    revokePreviewUrl(session);
    clearRecording(session);
    stopTracks(session);
    session.dotNetReference = null;
    session.disposed = true;
}

if (typeof window !== "undefined") {
    window.addEventListener("pagehide", () => {
        for (const session of activeSessions) {
            abortImmediately(session);
        }
        activeSessions.clear();
    });
}

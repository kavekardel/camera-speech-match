import { downsampleToPcm16 } from "./speech-session.js";

const maximumAudioBytes = 10 * 1024 * 1024;

// Owns only its audio graph; the recording session owns the shared media tracks.
export function createPcmRecording(stream, onError = () => {}) {
    let context = null;
    let source = null;
    let worklet = null;
    let gain = null;
    let state = "new";
    let chunks = [];
    let length = 0;
    let stopPromise = null;
    let stopResolve = null;
    let stopReject = null;
    let stopTimer = null;

    function receive(event) {
        if (state !== "recording" && state !== "stopping") {
            return;
        }
        if (event.data?.type === "stopped") {
            stopResolve?.();
            return;
        }
        if (!(event.data instanceof Float32Array)) {
            fail(new Error("Invalid PCM capture frame."));
            return;
        }
        const pcm = downsampleToPcm16(event.data, context.sampleRate, 16000);
        if (length + pcm.byteLength > maximumAudioBytes) {
            fail(new Error("PCM audio exceeds the 10 MiB limit."));
            return;
        }
        chunks.push(pcm);
        length += pcm.byteLength;
    }

    function fail(error) {
        if (state === "disposed") {
            return;
        }
        stopReject?.(error);
        void dispose();
        try { Promise.resolve(onError(error)).catch(() => {}); }
        catch { /* The recording session may already be gone. */ }
    }

    function dispose() {
        if (state === "disposed") {
            return Promise.resolve();
        }
        state = "disposed";
        clearTimeout(stopTimer);
        stopReject?.(new Error("PCM capture was disposed."));
        if (worklet) {
            worklet.port.onmessage = null;
            worklet.onprocessorerror = null;
        }
        source?.disconnect();
        worklet?.disconnect();
        gain?.disconnect();
        chunks = [];
        length = 0;
        return context?.close().catch(() => {}) ?? Promise.resolve();
    }

    return {
        async prepare() {
            if (state !== "new") {
                throw new Error("PCM capture cannot be prepared twice.");
            }
            state = "preparing";
            try {
                if (typeof AudioContext !== "function" || typeof AudioWorkletNode !== "function") {
                    throw new Error("PCM audio capture is not supported in this browser.");
                }
                context = new AudioContext({ latencyHint: "interactive" });
                await withTimeout(context.audioWorklet.addModule(new URL("./speech-input-worklet.js", import.meta.url)));
                if (state === "disposed") {
                    throw new Error("PCM capture was disposed.");
                }
                source = context.createMediaStreamSource(stream);
                worklet = new AudioWorkletNode(context, "stt-pcm-capture", { processorOptions: { controlled: true } });
                worklet.port.onmessage = receive;
                worklet.onprocessorerror = () => fail(new Error("The PCM capture processor failed."));
                gain = context.createGain();
                gain.gain.value = 0;
                source.connect(worklet);
                worklet.connect(gain);
                gain.connect(context.destination);
                await withTimeout(context.resume());
                if (state === "disposed" || context.state !== "running") {
                    throw new Error("PCM capture could not start.");
                }
                state = "ready";
            } catch (error) {
                await dispose();
                throw error;
            }
        },
        start() {
            if (state !== "ready") {
                throw new Error("PCM capture is not ready.");
            }
            state = "recording";
            worklet.port.postMessage({ type: "start" });
        },
        stop() {
            if (stopPromise) {
                return stopPromise;
            }
            if (state !== "recording") {
                return Promise.reject(new Error("PCM capture is not recording."));
            }
            state = "stopping";
            stopPromise = (async () => {
                try {
                    await new Promise((resolve, reject) => {
                        stopResolve = resolve;
                        stopReject = reject;
                        stopTimer = setTimeout(() => reject(new Error("PCM capture did not flush.")), 2000);
                        worklet.port.postMessage({ type: "stop" });
                    });
                    return new Blob(chunks, { type: "application/octet-stream" });
                } finally {
                    await dispose();
                }
            })();
            return stopPromise;
        },
        dispose
    };
}

async function withTimeout(promise) {
    let timer;
    try {
        return await Promise.race([promise, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error("PCM capture preparation timed out.")), 15000);
        })]);
    } finally {
        clearTimeout(timer);
    }
}

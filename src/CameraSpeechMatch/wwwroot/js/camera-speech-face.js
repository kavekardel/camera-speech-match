const defaults = Object.freeze({
    centerToleranceX: 0.12,
    centerToleranceY: 0.12,
    minFaceSize: 0.25,
    maxFaceSize: 0.65
});
const frameIntervalMilliseconds = 100;
const staleFrameMilliseconds = 1000;

export function normalizeFaceOptions(options) {
    const tolerance = (value, fallback) =>
        Number.isFinite(value) && value >= 0 && value <= 0.5 ? value : fallback;
    const validSizes = Number.isFinite(options?.minFaceSize) && Number.isFinite(options?.maxFaceSize) &&
        options.minFaceSize > 0 && options.minFaceSize <= options.maxFaceSize && options.maxFaceSize <= 1;

    return {
        centerToleranceX: tolerance(options?.centerToleranceX, defaults.centerToleranceX),
        centerToleranceY: tolerance(options?.centerToleranceY, defaults.centerToleranceY),
        minFaceSize: validSizes ? options.minFaceSize : defaults.minFaceSize,
        maxFaceSize: validSizes ? options.maxFaceSize : defaults.maxFaceSize
    };
}

// Match the existing centered object-fit: cover and scaleX(-1) preview.
export function projectFaceBounds(bounds, sourceWidth, sourceHeight, previewWidth, previewHeight) {
    const scale = Math.max(previewWidth / sourceWidth, previewHeight / sourceHeight);
    const offsetX = (previewWidth - sourceWidth * scale) / 2;
    const offsetY = (previewHeight - sourceHeight * scale) / 2;
    return {
        left: 1 - (bounds.maxX * sourceWidth * scale + offsetX) / previewWidth,
        right: 1 - (bounds.minX * sourceWidth * scale + offsetX) / previewWidth,
        top: (bounds.minY * sourceHeight * scale + offsetY) / previewHeight,
        bottom: (bounds.maxY * sourceHeight * scale + offsetY) / previewHeight
    };
}

export function evaluateFacePosition(bounds, sourceWidth, sourceHeight, previewWidth, previewHeight, options) {
    if (!bounds) {
        return "no-face";
    }
    const dimensions = [sourceWidth, sourceHeight, previewWidth, previewHeight];
    if (dimensions.some(value => !Number.isFinite(value) || value <= 0) ||
        [bounds.minX, bounds.maxX, bounds.minY, bounds.maxY].some(value => !Number.isFinite(value)) ||
        bounds.maxX <= bounds.minX || bounds.maxY <= bounds.minY) {
        return "outside";
    }

    const area = projectFaceBounds(bounds, ...dimensions);
    const config = normalizeFaceOptions(options);
    const epsilon = 1e-6;
    const centerX = (area.left + area.right) / 2;
    const centerY = (area.top + area.bottom) / 2;
    const height = area.bottom - area.top;
    const valid = evaluateFaceVisibility(bounds, ...dimensions) &&
        Math.abs(centerX - 0.5) <= config.centerToleranceX + epsilon &&
        Math.abs(centerY - 0.5) <= config.centerToleranceY + epsilon &&
        height >= config.minFaceSize - epsilon && height <= config.maxFaceSize + epsilon;
    return valid ? "valid" : "outside";
}

// Visibility is mandatory; centering and face size are only positioning advice.
export function evaluateFaceVisibility(bounds, sourceWidth, sourceHeight, previewWidth, previewHeight) {
    if (!bounds || [sourceWidth, sourceHeight, previewWidth, previewHeight].some(value =>
        !Number.isFinite(value) || value <= 0) ||
        [bounds.minX, bounds.maxX, bounds.minY, bounds.maxY].some(value => !Number.isFinite(value)) ||
        bounds.maxX <= bounds.minX || bounds.maxY <= bounds.minY) {
        return false;
    }
    const area = projectFaceBounds(bounds, sourceWidth, sourceHeight, previewWidth, previewHeight);
    const epsilon = 1e-6;
    return area.left >= -epsilon && area.right <= 1 + epsilon &&
        area.top >= -epsilon && area.bottom <= 1 + epsilon;
}

// Capture timestamps, rather than callback counts, define the validation period.
export function createFaceVisibilityValidation() {
    let lastInferenceAt = -Infinity;
    let visible = false;
    let startedAt = null;
    let stoppedAt = null;
    let lossSince = null;
    let errorCode = null;

    function check(timestamp) {
        if (startedAt === null) {
            return;
        }
        if (timestamp - lastInferenceAt >= staleFrameMilliseconds ||
            lossSince !== null && timestamp - lossSince >= staleFrameMilliseconds) {
            errorCode ??= "face_visibility_lost";
        }
    }

    function isVisible(timestamp) {
        return visible && timestamp >= lastInferenceAt &&
            timestamp - lastInferenceAt < staleFrameMilliseconds;
    }

    return {
        isVisible,
        observe(timestamp, isFaceVisible) {
            if (!Number.isFinite(timestamp) || timestamp <= lastInferenceAt ||
                stoppedAt !== null && timestamp > stoppedAt) {
                return false;
            }
            check(timestamp);
            lastInferenceAt = timestamp;
            visible = isFaceVisible;
            if (startedAt !== null) {
                if (isFaceVisible) {
                    lossSince = null;
                } else {
                    lossSince ??= Math.max(startedAt, timestamp);
                }
            }
            return true;
        },
        start(timestamp) {
            if (startedAt !== null || errorCode || !isVisible(timestamp)) {
                return false;
            }
            startedAt = timestamp;
            return true;
        },
        check(timestamp) {
            if (stoppedAt === null) {
                check(timestamp);
            }
        },
        markInvisible(timestamp) {
            visible = false;
            if (startedAt !== null && stoppedAt === null) {
                lossSince ??= timestamp;
                check(timestamp);
            }
        },
        markUnavailable() {
            visible = false;
            errorCode = "face_detection_unavailable";
        },
        freeze(timestamp) {
            if (stoppedAt === null) {
                check(timestamp);
                stoppedAt = timestamp;
            }
        },
        result() {
            if (stoppedAt === null || startedAt === null) {
                return { passed: false, errorCode: "face_not_visible" };
            }
            check(stoppedAt);
            const failure = errorCode || (!isVisible(stoppedAt) ? "face_not_visible" : null);
            return { passed: !failure, errorCode: failure };
        }
    };
}

// Owns only inference resources. Camera tracks and the recorder belong to video-verification.js.
export function createFaceTracker(videoElement, options, onStatusChanged) {
    const config = normalizeFaceOptions(options);
    let worker = null;
    let stopped = false;
    let ready = false;
    let busy = false;
    let animationId = null;
    let timeoutId = null;
    let lastStatus = null;
    let lastNotifiedVisible = null;
    let lastVideoTime = -1;
    let lastCaptureAt = -Infinity;
    let lastFrameAt = performance.now();
    let pendingDimensions = null;
    const validation = createFaceVisibilityValidation();
    let finishing = false;
    let finishPromise = null;
    let finishResolve = null;
    let finishTimeoutId = null;

    function notify(status, isFaceVisible = false) {
        if (stopped || status === lastStatus && isFaceVisible === lastNotifiedVisible) {
            return;
        }
        lastStatus = status;
        lastNotifiedVisible = isFaceVisible;
        // Circuit disposal must not leave an unhandled inference callback rejection.
        try {
            Promise.resolve(onStatusChanged(status, isFaceVisible)).catch(() => {});
        } catch {
        }
    }

    function stop() {
        if (stopped) {
            return;
        }
        stopped = true;
        if (animationId !== null) {
            cancelAnimationFrame(animationId);
        }
        clearTimeout(timeoutId);
        if (finishResolve) {
            validation.markUnavailable();
            finishPending();
        }
        if (worker) {
            worker.onmessage = null;
            worker.onerror = null;
            worker.onmessageerror = null;
            // Termination releases the task's WASM/OffscreenCanvas even during initialization.
            worker.terminate();
            worker = null;
        }
    }

    function fail(error) {
        if (stopped) {
            return;
        }
        console.warn("[CameraSpeechMatch:Face] Detection unavailable.", error);
        validation.markUnavailable();
        notify("unavailable");
        stop();
    }

    async function capture(timestamp) {
        busy = true;
        lastCaptureAt = timestamp;
        lastVideoTime = videoElement.currentTime;
        const preview = videoElement.getBoundingClientRect();
        pendingDimensions = [videoElement.videoWidth, videoElement.videoHeight, preview.width, preview.height];
        let bitmap = null;
        try {
            timeoutId = setTimeout(() => fail("Frame capture or inference timed out."), 5000);
            // Bound inference input size without changing its aspect ratio or the recording stream.
            const ratio = Math.min(1, 640 / Math.max(videoElement.videoWidth, videoElement.videoHeight));
            bitmap = await createImageBitmap(videoElement, {
                resizeWidth: Math.max(1, Math.round(videoElement.videoWidth * ratio)),
                resizeHeight: Math.max(1, Math.round(videoElement.videoHeight * ratio))
            });
            if (stopped) {
                bitmap.close();
                return;
            }
            worker.postMessage({ type: "frame", bitmap, timestamp }, [bitmap]);
            bitmap = null; // Ownership transferred to the worker, which closes it in finally.
        } catch (error) {
            bitmap?.close();
            fail(error);
        }
    }

    function tick(timestamp) {
        if (stopped || finishing) {
            return;
        }
        animationId = requestAnimationFrame(tick);
        validation.check(timestamp);
        const live = videoElement.srcObject?.getVideoTracks().some(track => track.readyState === "live");
        if (!live) {
            fail("Camera video track ended.");
            return;
        }
        if (document.visibilityState === "hidden" || videoElement.readyState < 2) {
            validation.markInvisible(timestamp);
            notify("no-face");
            return;
        }
        if (videoElement.currentTime !== lastVideoTime) {
            lastFrameAt = timestamp;
            if (ready && !busy && timestamp - lastCaptureAt >= frameIntervalMilliseconds) {
                void capture(timestamp);
            }
        }
        if (timestamp - lastFrameAt >= staleFrameMilliseconds ||
            ready && !validation.isVisible(timestamp) && lastNotifiedVisible) {
            notify("no-face");
        }
    }

    notify("loading");
    try {
        if (typeof Worker !== "function" || typeof createImageBitmap !== "function" ||
            typeof OffscreenCanvas !== "function") {
            throw new Error("Browser face detection capabilities are unavailable.");
        }
        worker = new Worker(new URL("./camera-speech-face-worker.js", import.meta.url));
        worker.onerror = worker.onmessageerror = event => fail(event.message || "Worker failed.");
        worker.onmessage = event => {
            if (stopped) {
                return;
            }
            if (event.data.type === "ready") {
                clearTimeout(timeoutId);
                ready = true;
                notify("no-face");
            } else if (event.data.type === "result") {
                if (!busy || event.data.timestamp !== lastCaptureAt) {
                    return;
                }
                clearTimeout(timeoutId);
                busy = false;
                const live = document.visibilityState !== "hidden" && videoElement.readyState >= 2 &&
                    videoElement.srcObject?.getVideoTracks().some(track => track.readyState === "live");
                validation.check(performance.now());
                validation.observe(event.data.timestamp,
                    live && evaluateFaceVisibility(event.data.bounds, ...pendingDimensions));
                const isFaceVisible = live && validation.isVisible(performance.now());
                const fresh = performance.now() - event.data.timestamp < staleFrameMilliseconds;
                notify(live && fresh ? evaluateFacePosition(event.data.bounds, ...pendingDimensions, config) : "no-face",
                    isFaceVisible);
                if (finishing) {
                    finishPending();
                }
            } else if (event.data.type === "error") {
                fail(event.data.message);
            }
        };
        worker.postMessage({
            type: "initialize",
            assetRoot: new URL("../vendor/mediapipe/1.0.1/", import.meta.url).href
        });
        timeoutId = setTimeout(() => fail("Initialization timed out."), 15000);
        animationId = requestAnimationFrame(tick);
    } catch (error) {
        fail(error);
    }
    function finishPending() {
        clearTimeout(finishTimeoutId);
        const resolve = finishResolve;
        finishResolve = null;
        resolve?.(validation.result());
    }

    return {
        stop,
        startRecordingValidation() {
            const live = document.visibilityState !== "hidden" && videoElement.readyState >= 2 &&
                videoElement.srcObject?.getVideoTracks().some(track => track.readyState === "live");
            const started = !stopped && !finishing && live && validation.start(performance.now());
            if (!started) {
                notify("no-face");
            }
            return started;
        },
        finishRecordingValidation() {
            if (finishPromise) {
                return finishPromise;
            }
            const stoppedAt = performance.now();
            if (document.visibilityState === "hidden" || videoElement.readyState < 2 ||
                !videoElement.srcObject?.getVideoTracks().some(track => track.readyState === "live")) {
                validation.markInvisible(stoppedAt);
            }
            validation.freeze(stoppedAt);
            finishing = true;
            cancelAnimationFrame(animationId);
            if (!busy || stopped) {
                finishPromise = Promise.resolve(validation.result());
            } else {
                finishPromise = new Promise(resolve => {
                    finishResolve = resolve;
                    finishTimeoutId = setTimeout(() => {
                        validation.markUnavailable();
                        finishPending();
                    }, staleFrameMilliseconds);
                });
            }
            return finishPromise;
        }
    };
}

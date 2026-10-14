/* global Vision */
let landmarker = null;

self.onmessage = async event => {
    const message = event.data;
    try {
        if (message.type === "initialize") {
            importScripts(new URL("vision_bundle.js", message.assetRoot).href);
            const fileset = await Vision.FilesetResolver.forVisionTasks(new URL("wasm", message.assetRoot).href);
            landmarker = await Vision.FaceLandmarker.createFromOptions(fileset, {
                baseOptions: {
                    modelAssetPath: new URL("face_landmarker.task", message.assetRoot).href,
                    delegate: "CPU"
                },
                canvas: new OffscreenCanvas(1, 1),
                runningMode: "VIDEO",
                numFaces: 1,
                outputFaceBlendshapes: false,
                outputFacialTransformationMatrixes: false
            });
            self.postMessage({ type: "ready" });
        } else if (message.type === "frame") {
            if (!landmarker) {
                throw new Error("Face Landmarker is not initialized.");
            }
            const result = landmarker.detectForVideo(message.bitmap, message.timestamp);
            const landmarks = result.faceLandmarks[0];
            let bounds = null;
            if (landmarks?.length) {
                bounds = { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity };
                for (const landmark of landmarks) {
                    bounds.minX = Math.min(bounds.minX, landmark.x);
                    bounds.maxX = Math.max(bounds.maxX, landmark.x);
                    bounds.minY = Math.min(bounds.minY, landmark.y);
                    bounds.maxY = Math.max(bounds.maxY, landmark.y);
                }
            }
            // Release the frame before the main thread can terminate this worker at stop.
            message.bitmap.close();
            message.bitmap = null;
            self.postMessage({ type: "result", bounds, timestamp: message.timestamp });
        }
    } catch (error) {
        landmarker?.close();
        landmarker = null;
        self.postMessage({ type: "error", message: error?.message || "Face detection failed." });
    } finally {
        message.bitmap?.close();
    }
};

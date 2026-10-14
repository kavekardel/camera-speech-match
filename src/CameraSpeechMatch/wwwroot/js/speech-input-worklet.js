class SttPcmCaptureProcessor extends AudioWorkletProcessor {
    constructor(options = {}) {
        super();
        this.framesPerChunk = Math.max(128, Math.round(sampleRate * 0.04));
        this.buffer = new Float32Array(this.framesPerChunk);
        this.offset = 0;
        this.enabled = options.processorOptions?.controlled !== true;
        this.port.onmessage = event => {
            if (event.data?.type === "start") {
                this.offset = 0;
                this.enabled = true;
            } else if (event.data?.type === "stop") {
                this.enabled = false;
                if (this.offset > 0) {
                    const tail = this.buffer.slice(0, this.offset);
                    this.port.postMessage(tail, [tail.buffer]);
                    this.offset = 0;
                }
                this.port.postMessage({ type: "stopped" });
            }
        };
    }

    process(inputs) {
        const input = inputs[0]?.[0];
        if (!this.enabled || !input) {
            return true;
        }

        let inputOffset = 0;
        while (inputOffset < input.length) {
            const available = this.framesPerChunk - this.offset;
            const count = Math.min(available, input.length - inputOffset);
            this.buffer.set(input.subarray(inputOffset, inputOffset + count), this.offset);
            this.offset += count;
            inputOffset += count;

            if (this.offset === this.framesPerChunk) {
                const chunk = this.buffer;
                this.port.postMessage(chunk, [chunk.buffer]);
                this.buffer = new Float32Array(this.framesPerChunk);
                this.offset = 0;
            }
        }

        return true;
    }
}

registerProcessor("stt-pcm-capture", SttPcmCaptureProcessor);

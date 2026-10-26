// AudioWorklet processor for DTLN noise suppression (Rust/WASM)
// DTLN expects: 16kHz mono, 512-sample blocks, 128-sample hop

const DTLN_BLOCK_LEN = 512;
const DTLN_BLOCK_SHIFT = 128;
const DTLN_SAMPLE_RATE = 16000;
const SIZEOF_F32 = 4;

class DtlnProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this.ready = false;
        this.bypassed = false;
        this.engineId = -1;
        this.wasmInstance = null;

        // Resampling state
        this.contextSampleRate = options.processorOptions?.sampleRate || 48000;
        this.downsampleRatio = this.contextSampleRate / DTLN_SAMPLE_RATE;

        // Input ring buffer at 16kHz (accumulate downsampled audio)
        this.inBuf16k = new Float32Array(DTLN_BLOCK_LEN);
        this.inBuf16kWritePos = 0;

        // Output ring buffer at 16kHz (denoised audio to read from)
        this.outBuf16k = new Float32Array(DTLN_BLOCK_LEN * 2);
        this.outBuf16kReadPos = 0;
        this.outBuf16kAvailable = 0;

        // Residual fractional sample for downsampling
        this.downsamplePhase = 0;

        this.port.onmessage = (e) => this._handleMessage(e.data);
        this.port.postMessage({ type: 'request-wasm' });
    }

    async _handleMessage(msg) {
        if (msg.type === 'wasm-binary') {
            try {
                await this._initWasm(msg.wasmBinary);
            } catch (err) {
                this.port.postMessage({ type: 'error', error: err.message });
            }
        } else if (msg.type === 'bypass') {
            this.bypassed = msg.value === true;
        }
    }

    async _initWasm(wasmBinary) {
        const importObject = {
            env: {
                // Minimal stubs for emscripten
                emscripten_notify_memory_growth: () => {},
                __assert_fail: () => {},
                emscripten_resize_heap: () => 0,
                _emscripten_get_now: () => performance?.now?.() || Date.now(),
                emscripten_get_now: () => performance?.now?.() || Date.now(),
                __cxa_throw: () => {},
                abort: () => { throw new Error('WASM abort'); },
                _abort: () => { throw new Error('WASM _abort'); },
            },
            wasi_snapshot_preview1: {
                fd_write: () => 0,
                fd_seek: () => 0,
                fd_close: () => 0,
                proc_exit: () => {},
                environ_get: () => 0,
                environ_sizes_get: () => 0,
                clock_time_get: () => 0,
            },
        };

        // instantiate() resolves to {module, instance} for raw bytes, but to a bare Instance
        // when handed an already-compiled WebAssembly.Module.
        const result = await WebAssembly.instantiate(wasmBinary, importObject);
        this.wasmInstance = result.instance ?? result;

        // Call _initialize if it exists (emscripten ctors)
        if (this.wasmInstance.exports._initialize) {
            this.wasmInstance.exports._initialize();
        }

        this.engineId = this.wasmInstance.exports.dtln_create_wasm();
        this.wasmAudioBufPtr = this.wasmInstance.exports.dtln_get_audio_buffer(this.engineId);

        // emscripten_resize_heap is stubbed to fail, so the memory never grows and this view
        // stays valid. Caching it keeps _processBlock allocation-free on the audio thread.
        this.wasmHeapF32 = new Float32Array(this.wasmInstance.exports.memory.buffer);

        // Pre-fill input buffer
        this.inBuf16k.fill(0);
        this.inBuf16kWritePos = DTLN_BLOCK_LEN - DTLN_BLOCK_SHIFT; // start with silence prefix

        this.ready = true;
        this.port.postMessage({ type: 'ready' });
    }

    process(inputs, outputs) {
        const input = inputs[0]?.[0];
        const output = outputs[0]?.[0];
        if (!input || !output) return true;

        if (!this.ready || this.bypassed) {
            output.set(input);
            return true;
        }

        const ratio = this.downsampleRatio;

        // --- Downsample input (48kHz → 16kHz) using linear interpolation ---
        const inputLen = input.length; // typically 128 at 48kHz
        let phase = this.downsamplePhase;

        while (phase < inputLen) {
            const idx = Math.floor(phase);
            const frac = phase - idx;
            const s0 = input[idx];
            const s1 = idx + 1 < inputLen ? input[idx + 1] : s0;
            const sample16k = s0 + (s1 - s0) * frac;

            this.inBuf16k[this.inBuf16kWritePos++] = sample16k;

            // When we have a full block, process it
            if (this.inBuf16kWritePos >= DTLN_BLOCK_LEN) {
                this._processBlock();
                // Shift buffer by BLOCK_SHIFT (overlap)
                this.inBuf16k.copyWithin(0, DTLN_BLOCK_SHIFT);
                this.inBuf16kWritePos = DTLN_BLOCK_LEN - DTLN_BLOCK_SHIFT;
            }

            phase += ratio;
        }
        this.downsamplePhase = phase - inputLen;

        // --- Upsample output (16kHz → 48kHz) using linear interpolation ---
        const outLen = output.length;
        if (this.outBuf16kAvailable === 0) {
            // No denoised data yet — output silence
            output.fill(0);
        } else {
            const samplesNeeded16k = outLen / ratio;
            for (let i = 0; i < outLen; i++) {
                const pos16k = (i / ratio);
                const idx = Math.floor(pos16k);
                const frac = pos16k - idx;
                const readIdx = this.outBuf16kReadPos + idx;
                const s0 = readIdx < this.outBuf16k.length ? this.outBuf16k[readIdx] : 0;
                const s1 = readIdx + 1 < this.outBuf16k.length ? this.outBuf16k[readIdx + 1] : s0;
                output[i] = s0 + (s1 - s0) * frac;
            }
            const consumed = Math.ceil(samplesNeeded16k);
            this.outBuf16kReadPos += consumed;
            this.outBuf16kAvailable -= consumed;
            if (this.outBuf16kAvailable < 0) this.outBuf16kAvailable = 0;
        }

        return true;
    }

    _heapF32() {
        // A memory.grow would detach the cached view; rebuild rather than write into it.
        let heap = this.wasmHeapF32;
        if (!heap || heap.length === 0) {
            heap = new Float32Array(this.wasmInstance.exports.memory.buffer);
            this.wasmHeapF32 = heap;
        }
        return heap;
    }

    _processBlock() {
        // Copy 512 samples from inBuf16k → WASM audio buffer
        const heap = this._heapF32();
        const wasmOffset = this.wasmAudioBufPtr / SIZEOF_F32;
        heap.set(this.inBuf16k.subarray(0, DTLN_BLOCK_LEN), wasmOffset);

        // Run DTLN denoising
        this.wasmInstance.exports.dtln_denoise_wasm(this.engineId);

        // Read BLOCK_SHIFT denoised samples from WASM buffer. subarray, not slice — every
        // outBuf16k.set below copies immediately, and copyWithin touches a different buffer.
        const denoised = heap.subarray(wasmOffset, wasmOffset + DTLN_BLOCK_SHIFT);

        // Append to output ring buffer
        const writeStart = this.outBuf16kReadPos + this.outBuf16kAvailable;
        if (writeStart + DTLN_BLOCK_SHIFT <= this.outBuf16k.length) {
            this.outBuf16k.set(denoised, writeStart);
        } else {
            // Shift buffer to make room
            this.outBuf16k.copyWithin(0, this.outBuf16kReadPos);
            this.outBuf16kAvailable -= 0; // just reorganize
            const newWrite = this.outBuf16kAvailable;
            this.outBuf16kReadPos = 0;
            this.outBuf16k.set(denoised, newWrite);
        }
        this.outBuf16kAvailable += DTLN_BLOCK_SHIFT;
    }
}

registerProcessor('dtln-processor', DtlnProcessor);

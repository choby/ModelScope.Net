"""Bounded in-memory WAV transport and output normalization for ASR."""
import base64
import io
import wave

import numpy as np

MAX_AUDIO_BYTES = 20 * 1024 * 1024
MAX_AUDIO_SECONDS = 600
MAX_TEXT_UNITS = 65536
SAMPLE_RATE = 16000


def decode_asr_input(payload, parameters):
    if parameters or not isinstance(payload, dict) or set(payload) != {"audio"}:
        raise ValueError("ASR accepts exactly one inline WAV audio and no parameters")
    item = payload["audio"]
    if not isinstance(item, dict) or set(item) != {"mimeType", "data"}:
        raise ValueError("ASR audio must contain only mimeType and data")
    encoded = item["data"]
    if (item["mimeType"] != "audio/wav" or not isinstance(encoded, str) or
            len(encoded) > ((MAX_AUDIO_BYTES + 2) // 3) * 4):
        raise ValueError("ASR audio encoding is invalid or oversized")
    try:
        contents = base64.b64decode(encoded, validate=True)
    except (ValueError, TypeError) as error:
        raise ValueError("ASR audio data is not valid base64") from error
    if not contents or len(contents) > MAX_AUDIO_BYTES:
        raise ValueError("ASR audio bytes exceed the supported limit")
    try:
        with wave.open(io.BytesIO(contents), "rb") as source:
            channels, width, rate = source.getnchannels(), source.getsampwidth(), source.getframerate()
            frames, compression = source.getnframes(), source.getcomptype()
            if (channels != 1 or width != 2 or rate != SAMPLE_RATE or compression != "NONE" or
                    frames <= 0 or frames > SAMPLE_RATE * MAX_AUDIO_SECONDS):
                raise ValueError("ASR requires mono 16 kHz PCM16 WAV within the duration limit")
            pcm = source.readframes(frames)
            if len(pcm) != frames * 2 or source.readframes(1):
                raise ValueError("ASR WAV frame data is malformed")
    except (EOFError, wave.Error) as error:
        raise ValueError("ASR WAV cannot be decoded") from error
    waveform = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
    # Worker parameters are strings until the common coercion boundary.
    return waveform, {"audio_fs": str(SAMPLE_RATE)}


def normalize_asr_output(output):
    if (not isinstance(output, list) or len(output) != 1 or
            not isinstance(output[0], dict) or not isinstance(output[0].get("text"), str)):
        raise ValueError("ASR pipeline output must contain exactly one text result")
    text = output[0]["text"]
    if len(text.encode("utf-16-le", errors="surrogatepass")) // 2 > MAX_TEXT_UNITS:
        raise ValueError("ASR text output exceeds the supported limit")
    return {"text": text}

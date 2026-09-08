import base64
import io
import unittest
import wave
from unittest.mock import patch

import numpy as np

from audio_transport import decode_asr_input, normalize_asr_output


def wav_payload(rate=16000, channels=1, width=2, frames=160):
    target = io.BytesIO()
    with wave.open(target, "wb") as sink:
        sink.setnchannels(channels); sink.setsampwidth(width); sink.setframerate(rate)
        sink.writeframes(bytes(frames * channels * width))
    return {"audio": {"mimeType": "audio/wav", "data": base64.b64encode(target.getvalue()).decode()}}


class AudioTransportTests(unittest.TestCase):
    def test_decodes_supported_wave_to_normalized_float_samples(self):
        waveform, arguments = decode_asr_input(wav_payload(), {})
        self.assertEqual((160,), waveform.shape)
        self.assertEqual(np.float32, waveform.dtype)
        self.assertEqual({"audio_fs": "16000"}, arguments)

    def test_rejects_path_url_extra_fields_and_parameters(self):
        valid = wav_payload()
        for payload in ["audio.wav", "https://example.test/audio.wav", {"audio": "audio.wav"},
                        {"audio": {**valid["audio"], "path": "/tmp/x"}}]:
            with self.assertRaises(ValueError): decode_asr_input(payload, {})
        with self.assertRaises(ValueError): decode_asr_input(valid, {"hotword": "bypass"})

    def test_rejects_unsupported_wave_layout_and_limits(self):
        for payload in [wav_payload(rate=8000), wav_payload(channels=2), wav_payload(width=1)]:
            with self.assertRaises(ValueError): decode_asr_input(payload, {})
        with patch("audio_transport.MAX_AUDIO_SECONDS", 0):
            with self.assertRaises(ValueError): decode_asr_input(wav_payload(), {})
        with patch("audio_transport.MAX_AUDIO_BYTES", 8):
            with self.assertRaises(ValueError): decode_asr_input(wav_payload(), {})

    def test_normalizes_single_funasr_result(self):
        self.assertEqual({"text": "每一天都要快乐喔"}, normalize_asr_output([{"key": "sample", "text": "每一天都要快乐喔"}]))

    def test_rejects_malformed_or_oversized_output(self):
        for output in [{}, [], [{"text": "a"}, {"text": "b"}], [{"text": 1}]]:
            with self.assertRaises(ValueError): normalize_asr_output(output)
        with self.assertRaises(ValueError): normalize_asr_output([{"text": "x" * 65537}])


if __name__ == "__main__":
    unittest.main()

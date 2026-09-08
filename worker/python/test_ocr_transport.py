import base64
import io
import unittest
from unittest.mock import Mock, patch

import numpy as np
from PIL import Image

from ocr_transport import decode_ocr_input, normalize_ocr_output


def encoded_image(format="PNG", size=(2, 3)):
    with io.BytesIO() as buffer:
        Image.new("RGB", size, (255, 255, 255)).save(buffer, format=format)
        return base64.b64encode(buffer.getvalue()).decode("ascii")


class OcrTransportTests(unittest.TestCase):
    def test_decodes_png_and_jpeg_as_rgb_without_path_or_url(self):
        for mime, format in (("image/png", "PNG"), ("image/jpeg", "JPEG")):
            image, size = decode_ocr_input({"image": {"mimeType": mime, "data": encoded_image(format)}}, {})
            self.assertEqual(((2, 3), "RGB"), (size, image.mode))

    def test_rejects_invalid_schema_encoding_and_parameters(self):
        valid = {"image": {"mimeType": "image/png", "data": encoded_image()}}
        invalid = [None, "path.png", {}, {"image": "path.png"},
                   {"image": {"mimeType": "image/gif", "data": "AAAA"}},
                   {"image": {"mimeType": "image/png", "data": "not base64"}},
                   {"image": {"mimeType": "image/jpeg", "data": encoded_image("PNG")}},
                   {**valid, "url": "https://example.invalid"}]
        for payload in invalid:
            with self.subTest(payload_type=type(payload).__name__), self.assertRaises(ValueError):
                decode_ocr_input(payload, {})
        with self.assertRaises(ValueError):
            decode_ocr_input(valid, {"batch_size": "4"})

    def test_rejects_pixel_and_encoded_byte_limits_before_pipeline(self):
        valid = {"image": {"mimeType": "image/png", "data": encoded_image()}}
        with patch("ocr_transport.MAX_IMAGE_PIXELS", 5), self.assertRaises(ValueError):
            decode_ocr_input(valid, {})
        with patch("ocr_transport.MAX_IMAGE_BYTES", 8), self.assertRaises(ValueError):
            decode_ocr_input(valid, {})

    def test_normalizes_recognition_and_detection_outputs(self):
        self.assertEqual({"text": ["模型"]}, normalize_ocr_output("ocr-recognition", {"text": "模型"}))
        self.assertEqual({"text": ["模型"]}, normalize_ocr_output("ocr-recognition", {"text": ["模型"]}))
        output = normalize_ocr_output("ocr-detection", {"polygons": np.array([[0, 0, 2, 0, 2, 1, 0, 1]])})
        self.assertEqual({"polygons": [[0, 0, 2, 0, 2, 1, 0, 1]]}, output)

    def test_rejects_invalid_or_unbounded_outputs(self):
        invalid_text = [None, 42, "x" * 65537, [], ["a", "b"], [42]]
        for value in invalid_text:
            with self.assertRaises(ValueError): normalize_ocr_output("ocr-recognition", {"text": value})
        invalid_polygons = [None, [[0] * 7], [[0] * 9], [[0, 0, 1, 0, 1, 1, 0, float("nan")]],
                            [[False, 0, 1, 0, 1, 1, 0, 1]]]
        for value in invalid_polygons:
            with self.assertRaises(ValueError): normalize_ocr_output("ocr-detection", {"polygons": value})

    def test_worker_decodes_before_call_and_normalizes_result(self):
        from server import ModelScopePipelineModel
        for task, raw, expected in (
            ("ocr-recognition", {"text": ["ABC"]}, {"text": ["ABC"]}),
            ("ocr-detection", {"polygons": np.array([[0] * 8])}, {"polygons": [[0] * 8]}),
        ):
            model = ModelScopePipelineModel.__new__(ModelScopePipelineModel)
            model._task = task
            model._pipeline = Mock(return_value=raw)
            payload = {"image": {"mimeType": "image/png", "data": encoded_image()}}
            self.assertEqual(expected, model.invoke(payload, {}))
            self.assertIsInstance(model._pipeline.call_args.args[0], Image.Image)
            model._pipeline.reset_mock()
            with self.assertRaises(ValueError): model.invoke("path.png", {})
            model._pipeline.assert_not_called()


if __name__ == "__main__":
    unittest.main()

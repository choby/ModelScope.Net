import base64
import io
import unittest
from unittest.mock import patch

import numpy as np
from PIL import Image
from image_output import encode_generated_images, LimitedBuffer, validate_generation_input


class ImageOutputTests(unittest.TestCase):
    def test_image_loader_requests_safetensors_without_changing_other_tasks(self):
        import types
        from unittest.mock import Mock
        from server import ModelScopePipelineModel
        package = types.ModuleType("modelscope")
        pipelines = types.ModuleType("modelscope.pipelines")
        pipelines.pipeline = Mock()
        with patch.dict("sys.modules", {"modelscope": package, "modelscope.pipelines": pipelines}):
            ModelScopePipelineModel("/snapshot", "text-to-image-synthesis", False)
            pipelines.pipeline.assert_called_once_with(model="/snapshot", trust_remote_code=False,
                                                       task="text-to-image-synthesis", use_safetensors=True)
            pipelines.pipeline.reset_mock()
            ModelScopePipelineModel("/snapshot", "text-classification", False)
            pipelines.pipeline.assert_called_once_with(model="/snapshot", trust_remote_code=False,
                                                       task="text-classification")

    def test_seed_uses_fresh_local_generator_without_mutating_global_rng(self):
        import torch
        from image_output import prepare_generation_input
        before = torch.random.get_rng_state().clone()
        payload = {"text": "cube", "seed": 42}
        first = prepare_generation_input(payload, {})
        second = prepare_generation_input(payload, {})
        self.assertNotIn("seed", first)
        self.assertEqual(42, payload["seed"])
        self.assertIsNot(first["generator"], second["generator"])
        self.assertTrue(torch.equal(torch.randn(10, generator=first["generator"]),
                                    torch.randn(10, generator=second["generator"])))
        self.assertTrue(torch.equal(before, torch.random.get_rng_state()))
        self.assertNotIn("generator", prepare_generation_input({"text": "cube", "seed": None}, {}))

    def test_seed_bounds(self):
        for seed in (-1, True, 1.5, "42", 9223372036854775808):
            with self.subTest(seed=seed), self.assertRaises(ValueError):
                validate_generation_input({"text": "cube", "seed": seed}, {})
        for seed in (0, 9223372036854775807):
            self.assertEqual(seed, validate_generation_input({"text": "cube", "seed": seed}, {})["seed"])

    def test_input_defaults_and_no_mutation(self):
        payload = {"text": "cube"}
        result = validate_generation_input(payload, {})
        self.assertEqual({"text": "cube"}, payload)
        self.assertEqual((512, 512, 30, 1), tuple(result[key] for key in
                         ("width", "height", "num_inference_steps", "num_images_per_prompt")))

    def test_input_rejects_bypass_and_invalid_values(self):
        for change in ({"width": True}, {"height": 65}, {"width": 2056},
                       {"num_inference_steps": 101}, {"num_images_per_prompt": 5},
                       {"width": 2048, "height": 2048, "num_images_per_prompt": 2},
                       {"guidance_scale": float("nan")}, {"guidance_scale": True},
                       {"guidance_scale": 10**1000}, {"output_type": "np"},
                       {"return_dict": 1}, {"generator": "path"}, {"text": ["cube"]},
                       {"negative_prompt": "😀" * 2049}):
            with self.subTest(keys=list(change)):
                with self.assertRaises(ValueError):
                    validate_generation_input({"text": "cube", **change}, {})
        with self.assertRaises(ValueError):
            validate_generation_input({"text": "cube"}, {"num_inference_steps": "1000"})

    def test_worker_rejects_before_pipeline_and_normalizes_valid_request(self):
        from server import ModelScopePipelineModel
        from unittest.mock import Mock
        model = ModelScopePipelineModel.__new__(ModelScopePipelineModel)
        model._task = "text-to-image-synthesis"
        model._pipeline = Mock(return_value={"output_imgs": [Image.new("RGB", (1, 1))]})
        with self.assertRaises(ValueError):
            model.invoke({"text": "cube", "num_inference_steps": 1000}, {})
        model._pipeline.assert_not_called()
        self.assertEqual(1, len(model.invoke({"text": "cube"}, {})["images"]))
        self.assertEqual(30, model._pipeline.call_args.args[0]["num_inference_steps"])

    def test_stream_rejects_invalid_input_before_inference(self):
        from server import ModelScopePipelineModel
        from unittest.mock import Mock
        model = ModelScopePipelineModel.__new__(ModelScopePipelineModel)
        model._task = "text-to-image-synthesis"
        model._pipeline = Mock()
        with self.assertRaises(ValueError):
            list(model.stream({"text": "cube"}, {"width": "4096"}))
        model._pipeline.assert_not_called()

    def test_non_image_tasks_keep_existing_payload_contract(self):
        from server import ModelScopePipelineModel
        from unittest.mock import Mock
        model = ModelScopePipelineModel.__new__(ModelScopePipelineModel)
        model._task = "text-classification"
        model._pipeline = Mock(return_value={"labels": ["positive"]})
        self.assertEqual({"labels": ["positive"]}, model.invoke("good", {"top_k": "1"}))
        model._pipeline.assert_called_once_with("good", top_k=1)

    def test_bgr_array_becomes_red_png_not_blue(self):
        result = encode_generated_images({"output_imgs": [np.array([[[0, 0, 255]]], dtype=np.uint8)]})
        item = result["images"][0]
        self.assertEqual((1, 1, "image/png"), (item["width"], item["height"], item["mimeType"]))
        with Image.open(io.BytesIO(base64.b64decode(item["data"], validate=True))) as image:
            self.assertEqual((255, 0, 0), image.getpixel((0, 0)))

    def test_pil_rgb_is_not_channel_swapped(self):
        result = encode_generated_images({"output_imgs": [Image.new("RGB", (2, 2), (255, 0, 0))]})
        with Image.open(io.BytesIO(base64.b64decode(result["images"][0]["data"]))) as image:
            self.assertEqual((255, 0, 0), image.getpixel((0, 0)))

    def test_invalid_shapes_types_and_counts_fail(self):
        for images in ([], [None], [np.zeros((1, 1, 3))], [np.zeros((1, 1, 4), dtype=np.uint8)],
                       [np.zeros((0, 1, 3), dtype=np.uint8)], [Image.new("RGB", (1, 1))] * 5):
            with self.subTest(images_type=type(images[0]).__name__ if images else "empty"):
                with self.assertRaises(ValueError):
                    encode_generated_images({"output_imgs": images})

    def test_total_pixel_limit_is_checked_before_encoding(self):
        with patch("image_output.MAX_TOTAL_PIXELS", 3):
            with self.assertRaises(ValueError):
                encode_generated_images({"output_imgs": [Image.new("RGB", (2, 2))]})

    def test_encoded_limit_and_buffer_boundary(self):
        with LimitedBuffer(3) as buffer:
            buffer.write(b"abc")
            with self.assertRaises(ValueError): buffer.write(b"d")
        with patch("image_output.MAX_TOTAL_PNG_BYTES", 8):
            with self.assertRaises(ValueError):
                encode_generated_images({"output_imgs": [Image.new("RGB", (1, 1))]})


if __name__ == "__main__":
    unittest.main()

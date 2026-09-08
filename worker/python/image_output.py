"""Bounded image transport for ModelScope text-to-image pipeline results.

ModelScope output_imgs ndarray values are BGR uint8. PIL values are already RGB.
Imports stay lazy so the protocol-only image does not require model dependencies.
"""
import base64
import io
import math

MAX_IMAGES = 4
MAX_TOTAL_PIXELS = 4 * 1024 * 1024
MAX_TOTAL_PNG_BYTES = 2 * 1024 * 1024


def validate_generation_input(payload, parameters):
    """Validate before inference; limits are admission bounds, not memory guarantees."""
    allowed = {"text", "negative_prompt", "width", "height", "num_inference_steps",
               "guidance_scale", "num_images_per_prompt", "output_type", "return_dict", "seed"}
    if not isinstance(payload, dict) or parameters or set(payload) - allowed:
        raise ValueError("unsupported image generation inputs or parameters")
    prompt = payload.get("text")
    negative = payload.get("negative_prompt")
    # Match .NET string.Length, which counts UTF-16 code units.
    def text_length(value):
        return len(value.encode("utf-16-le", errors="surrogatepass")) // 2
    if (not isinstance(prompt, str) or not prompt.strip() or text_length(prompt) > 4096 or
            (negative is not None and (not isinstance(negative, str) or text_length(negative) > 4096))):
        raise ValueError("invalid image generation prompt")
    normalized = {"width": 512, "height": 512, "num_inference_steps": 30,
                  "guidance_scale": 7.5, "num_images_per_prompt": 1,
                  "output_type": "pil", "return_dict": True, **payload}
    width, height, steps, count = (normalized[key] for key in
                                  ("width", "height", "num_inference_steps", "num_images_per_prompt"))
    guidance = normalized["guidance_scale"]
    seed = normalized.get("seed")
    if seed is not None and (type(seed) is not int or not 0 <= seed <= 9223372036854775807):
        raise ValueError("image generation seed must be a non-negative signed 64-bit integer")
    if (any(type(value) is not int for value in (width, height, steps, count)) or
            not 64 <= width <= 2048 or not 64 <= height <= 2048 or width % 8 or height % 8 or
            not 1 <= count <= MAX_IMAGES or width * height * count > MAX_TOTAL_PIXELS or
            not 1 <= steps <= 100 or type(guidance) not in (float, int) or
            not 0 <= guidance <= 30 or not math.isfinite(guidance) or
            normalized["output_type"] != "pil" or normalized["return_dict"] is not True):
        raise ValueError("image generation inputs exceed supported sampling bounds")
    return normalized


def prepare_generation_input(payload, parameters):
    normalized = validate_generation_input(payload, parameters)
    seed = normalized.pop("seed", None)
    if seed is not None:
        import torch
        # A new local generator for every request; never reseed the global RNG.
        # CPU generator support on other execution devices is model-dependent.
        normalized["generator"] = torch.Generator(device="cpu").manual_seed(seed)
    return normalized


class LimitedBuffer(io.BytesIO):
    def __init__(self, maximum):
        super().__init__()
        self.maximum = maximum

    def write(self, value):
        if self.tell() + len(value) > self.maximum:
            raise ValueError("generated image exceeds encoded output limit")
        return super().write(value)


def encode_generated_images(output):
    from PIL import Image
    import numpy as np

    if not isinstance(output, dict) or not isinstance(output.get("output_imgs"), (list, tuple)):
        raise ValueError("image pipeline must return an output_imgs sequence")
    images = output["output_imgs"]
    if not 1 <= len(images) <= MAX_IMAGES:
        raise ValueError("generated image count exceeds supported limits")
    # Validate the entire result before converting any pixel buffers.
    dimensions = []
    pixels = 0
    for value in images:
        if isinstance(value, Image.Image):
            width, height = value.size
            if value.mode not in ("RGB", "RGBA", "L"):
                raise ValueError("unsupported generated PIL image mode")
        elif isinstance(value, np.ndarray):
            if value.dtype != np.uint8 or value.ndim != 3 or value.shape[2] != 3:
                raise ValueError("generated array must contain BGR uint8 pixels")
            height, width = value.shape[:2]
        else:
            raise ValueError("unsupported generated image value")
        pixels += width * height
        if width <= 0 or height <= 0 or pixels > MAX_TOTAL_PIXELS:
            raise ValueError("generated image exceeds pixel limit")
        dimensions.append((width, height))
    result = []
    encoded_bytes = 0
    for value, (width, height) in zip(images, dimensions):
        image = value if isinstance(value, Image.Image) else Image.fromarray(value[:, :, ::-1])
        with LimitedBuffer(MAX_TOTAL_PNG_BYTES - encoded_bytes) as buffer:
            image.save(buffer, format="PNG")
            png = buffer.getvalue()
        encoded_bytes += len(png)
        result.append({"mimeType": "image/png", "data": base64.b64encode(png).decode("ascii"),
                       "width": width, "height": height})
    return {"images": result}

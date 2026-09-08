"""Bounded in-memory image transport and output normalization for OCR tasks."""
import base64
import io
import math

MAX_IMAGE_BYTES = 8 * 1024 * 1024
MAX_IMAGE_PIXELS = 16 * 1024 * 1024
MAX_DIMENSION = 8192
MAX_POLYGONS = 10000
MAX_TEXT_UNITS = 65536
MIME_FORMATS = {"image/png": "PNG", "image/jpeg": "JPEG"}


def decode_ocr_input(payload, parameters):
    from PIL import Image

    if parameters or not isinstance(payload, dict) or set(payload) != {"image"}:
        raise ValueError("OCR accepts exactly one inline image and no parameters")
    item = payload["image"]
    if not isinstance(item, dict) or set(item) != {"mimeType", "data"}:
        raise ValueError("OCR image must contain only mimeType and data")
    mime = item["mimeType"]
    encoded = item["data"]
    if mime not in MIME_FORMATS or not isinstance(encoded, str) or len(encoded) > ((MAX_IMAGE_BYTES + 2) // 3) * 4:
        raise ValueError("OCR image encoding is invalid or oversized")
    try:
        contents = base64.b64decode(encoded, validate=True)
    except (ValueError, TypeError) as error:
        raise ValueError("OCR image data is not valid base64") from error
    if not contents or len(contents) > MAX_IMAGE_BYTES:
        raise ValueError("OCR image bytes exceed the supported limit")
    try:
        with Image.open(io.BytesIO(contents)) as source:
            width, height = source.size
            if (source.format != MIME_FORMATS[mime] or width <= 0 or height <= 0 or
                    width > MAX_DIMENSION or height > MAX_DIMENSION or width * height > MAX_IMAGE_PIXELS or
                    getattr(source, "is_animated", False)):
                raise ValueError("OCR image format or dimensions are unsupported")
            source.load()
            image = source.convert("RGB")
    except (OSError, Image.DecompressionBombError) as error:
        raise ValueError("OCR image cannot be decoded") from error
    return image, (width, height)


def normalize_ocr_output(task, output):
    if not isinstance(output, dict):
        raise ValueError("OCR pipeline output must be an object")
    if task == "ocr-recognition":
        source = output.get("text")
        texts = [source] if isinstance(source, str) else source
        if (not isinstance(texts, list) or len(texts) != 1 or not isinstance(texts[0], str) or
                len(texts[0].encode("utf-16-le", errors="surrogatepass")) // 2 > MAX_TEXT_UNITS):
            raise ValueError("OCR recognition output is invalid or oversized")
        return {"text": texts}
    polygons = output.get("polygons")
    if hasattr(polygons, "tolist"):
        polygons = polygons.tolist()
    if not isinstance(polygons, list) or len(polygons) > MAX_POLYGONS:
        raise ValueError("OCR detection polygons are invalid or oversized")
    normalized = []
    coordinate_count = 0
    for polygon in polygons:
        if not isinstance(polygon, list) or len(polygon) < 8 or len(polygon) > 64 or len(polygon) % 2:
            raise ValueError("OCR detection polygon shape is invalid")
        coordinate_count += len(polygon)
        if coordinate_count > MAX_POLYGONS * 8:
            raise ValueError("OCR detection coordinates exceed the supported limit")
        row = []
        for coordinate in polygon:
            if isinstance(coordinate, bool) or not isinstance(coordinate, (int, float)) or not math.isfinite(coordinate):
                raise ValueError("OCR detection coordinate is invalid")
            row.append(coordinate)
        normalized.append(row)
    return {"polygons": normalized}

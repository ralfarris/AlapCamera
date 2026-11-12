"""Pre-trained AI model management.

AlapCamera uses Google's pre-trained MediaPipe models, so **no dataset or
training is required**. The models are downloaded once on first use and
cached in ``models/`` next to the project.
"""
from __future__ import annotations

import os
import urllib.request
from pathlib import Path

MODEL_DIR = Path(os.environ.get("ALAPCAMERA_MODELS", Path(__file__).resolve().parent.parent / "models"))

_BASE = "https://storage.googleapis.com/mediapipe-models"
MODELS = {
    "face": ("blaze_face_short_range.tflite",
             f"{_BASE}/face_detector/blaze_face_short_range/float16/latest/blaze_face_short_range.tflite"),
    "selfie": ("selfie_segmenter.tflite",
               f"{_BASE}/image_segmenter/selfie_segmenter/float16/latest/selfie_segmenter.tflite"),
    "multiclass": ("selfie_multiclass_256x256.tflite",
                   f"{_BASE}/image_segmenter/selfie_multiclass_256x256/float32/latest/selfie_multiclass_256x256.tflite"),
}


def get_model(key: str) -> Path:
    """Return the local path of a model, downloading it if necessary."""
    filename, url = MODELS[key]
    path = MODEL_DIR / filename
    if path.exists() and path.stat().st_size > 0:
        return path
    MODEL_DIR.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".part")
    print(f"Downloading model {filename} ...")
    urllib.request.urlretrieve(url, tmp)
    tmp.replace(path)
    return path

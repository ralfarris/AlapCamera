"""Frame processing pipeline.

Each processor is a callable object with ``enabled`` flag and a
``process(frame) -> frame`` method. Processors are applied in order.
"""
from __future__ import annotations

import threading
from typing import Protocol

import cv2
import numpy as np


class Processor(Protocol):
    name: str
    enabled: bool

    def process(self, frame: np.ndarray) -> np.ndarray: ...


class MirrorProcessor:
    name = "mirror"

    def __init__(self, enabled: bool = False):
        self.enabled = enabled

    def process(self, frame: np.ndarray) -> np.ndarray:
        return cv2.flip(frame, 1)


class Pipeline:
    def __init__(self) -> None:
        self._processors: list[Processor] = []
        self._lock = threading.Lock()

    def add(self, processor: Processor) -> Processor:
        with self._lock:
            self._processors.append(processor)
        return processor

    def get(self, name: str) -> Processor | None:
        for p in self._processors:
            if p.name == name:
                return p
        return None

    def process(self, frame: np.ndarray) -> np.ndarray:
        with self._lock:
            procs = list(self._processors)
        for p in procs:
            if p.enabled:
                frame = p.process(frame)
        return frame

    def close(self) -> None:
        for p in self._processors:
            close = getattr(p, "close", None)
            if callable(close):
                close()

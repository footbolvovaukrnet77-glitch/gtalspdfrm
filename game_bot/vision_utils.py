"""
vision_utils.py — общие вспомогательные функции компьютерного зрения.

Используется player_detector.py и obstacle_detector.py.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Sequence, Tuple

import cv2
import numpy as np


@dataclass
class DetectedObject:
    """Найденный на кадре объект (игрок или препятствие)."""

    x: int
    y: int
    width: int
    height: int
    area: float = 0.0
    kind: str = "object"
    confidence: float = 1.0
    extra: Dict[str, Any] = field(default_factory=dict)

    # -- производные свойства -------------------------------------------
    @property
    def center_x(self) -> int:
        return int(self.x + self.width / 2)

    @property
    def center_y(self) -> int:
        return int(self.y + self.height / 2)

    @property
    def left(self) -> int:
        return int(self.x)

    @property
    def right(self) -> int:
        return int(self.x + self.width)

    @property
    def top(self) -> int:
        return int(self.y)

    @property
    def bottom(self) -> int:
        return int(self.y + self.height)

    def as_tuple(self) -> Tuple[int, int, int, int]:
        return (self.x, self.y, self.width, self.height)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "x": self.x,
            "y": self.y,
            "width": self.width,
            "height": self.height,
            "center_x": self.center_x,
            "center_y": self.center_y,
            "area": self.area,
            "kind": self.kind,
            "confidence": self.confidence,
        }

    # Удобно для отладки / логирования
    def __getitem__(self, key: str) -> Any:
        return self.to_dict()[key]


# ----------------------------------------------------------------------
def to_hsv(frame: np.ndarray) -> np.ndarray:
    """BGR -> HSV."""
    return cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)


def color_mask(
    hsv: np.ndarray,
    lower: Sequence[int],
    upper: Sequence[int],
    extra_ranges: Optional[Sequence[Dict[str, Sequence[int]]]] = None,
) -> np.ndarray:
    """
    Построить бинарную маску по одному или нескольким HSV-диапазонам.
    Корректно обрабатывает "заворот" оттенка (например, красный: 170..10).
    """
    mask = _single_range_mask(hsv, lower, upper)
    for rng in extra_ranges or []:
        try:
            extra = _single_range_mask(hsv, rng["lower"], rng["upper"])
        except (KeyError, TypeError):
            continue
        mask = cv2.bitwise_or(mask, extra)
    return mask


def _single_range_mask(
    hsv: np.ndarray, lower: Sequence[int], upper: Sequence[int]
) -> np.ndarray:
    lo = np.array([int(v) for v in lower], dtype=np.uint8)
    hi = np.array([int(v) for v in upper], dtype=np.uint8)
    if int(lo[0]) <= int(hi[0]):
        return cv2.inRange(hsv, lo, hi)
    # Оттенок "заворачивается" через 0
    lo1 = np.array([int(lo[0]), int(lo[1]), int(lo[2])], dtype=np.uint8)
    hi1 = np.array([179, int(hi[1]), int(hi[2])], dtype=np.uint8)
    lo2 = np.array([0, int(lo[1]), int(lo[2])], dtype=np.uint8)
    hi2 = np.array([int(hi[0]), int(hi[1]), int(hi[2])], dtype=np.uint8)
    return cv2.bitwise_or(cv2.inRange(hsv, lo1, hi1), cv2.inRange(hsv, lo2, hi2))


def clean_mask(mask: np.ndarray, kernel_size: int = 5, iterations: int = 1) -> np.ndarray:
    """Морфологическая очистка маски: убрать шум, закрыть дыры."""
    k = max(1, int(kernel_size))
    if k % 2 == 0:
        k += 1
    kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))
    out = cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel, iterations=iterations)
    out = cv2.morphologyEx(out, cv2.MORPH_CLOSE, kernel, iterations=iterations)
    return out


def find_boxes(mask: np.ndarray) -> List[Tuple[int, int, int, int, float]]:
    """Контуры -> список (x, y, w, h, area)."""
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    boxes: List[Tuple[int, int, int, int, float]] = []
    for contour in contours:
        area = float(cv2.contourArea(contour))
        if area <= 0:
            continue
        x, y, w, h = cv2.boundingRect(contour)
        boxes.append((int(x), int(y), int(w), int(h), area))
    return boxes


def boxes_overlap(
    a: Tuple[int, int, int, int], b: Tuple[int, int, int, int], gap: int = 0
) -> bool:
    ax, ay, aw, ah = a
    bx, by, bw, bh = b
    if ax > bx + bw + gap or bx > ax + aw + gap:
        return False
    if ay > by + bh + gap or by > ay + ah + gap:
        return False
    return True


def merge_boxes(
    boxes: Sequence[Tuple[int, int, int, int, float]], gap: int = 0
) -> List[Tuple[int, int, int, int, float]]:
    """
    Склеить пересекающиеся/близкие прямоугольники (куски одного объекта:
    планка + стойки образуют одно препятствие).
    """
    items = [list(b) for b in boxes]
    merged = True
    while merged:
        merged = False
        result: List[List[float]] = []
        while items:
            current = items.pop()
            changed = True
            while changed:
                changed = False
                remaining: List[List[float]] = []
                for other in items:
                    if boxes_overlap(
                        (int(current[0]), int(current[1]), int(current[2]), int(current[3])),
                        (int(other[0]), int(other[1]), int(other[2]), int(other[3])),
                        gap,
                    ):
                        x1 = min(current[0], other[0])
                        y1 = min(current[1], other[1])
                        x2 = max(current[0] + current[2], other[0] + other[2])
                        y2 = max(current[1] + current[3], other[1] + other[3])
                        current = [x1, y1, x2 - x1, y2 - y1, current[4] + other[4]]
                        changed = True
                        merged = True
                    else:
                        remaining.append(other)
                items = remaining
            result.append(current)
        items = result
    return [(int(b[0]), int(b[1]), int(b[2]), int(b[3]), float(b[4])) for b in items]


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))

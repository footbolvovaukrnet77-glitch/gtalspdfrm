"""
player_detector.py — поиск персонажа на кадре.

Персонаж ищется по HSV-маске (config.PLAYER_HSV_*) в нижней части кадра,
с фильтрацией по площади, соотношению сторон и положению.
Если персонаж временно потерян — возвращается последнее известное положение.
"""

from __future__ import annotations

from typing import List, Optional, Tuple

import cv2
import numpy as np

import config
from vision_utils import (
    DetectedObject,
    clean_mask,
    color_mask,
    find_boxes,
    merge_boxes,
    to_hsv,
)


class PlayerDetector:
    """Детектор игрока с памятью последнего положения."""

    def __init__(self) -> None:
        self.last_player: Optional[DetectedObject] = None
        self.frames_since_seen: int = 0
        self.last_mask: Optional[np.ndarray] = None
        self.detected_this_frame: bool = False

    # ------------------------------------------------------------------
    def reset(self) -> None:
        self.last_player = None
        self.frames_since_seen = 0
        self.last_mask = None
        self.detected_this_frame = False

    # ------------------------------------------------------------------
    def build_mask(self, frame: np.ndarray) -> np.ndarray:
        hsv = to_hsv(frame)
        mask = color_mask(
            hsv,
            config.PLAYER_HSV_LOWER,
            config.PLAYER_HSV_UPPER,
            config.PLAYER_HSV_EXTRA_RANGES,
        )
        # Игрок всегда в нижней части экрана — верх обнуляем.
        height = frame.shape[0]
        cut = int(height * float(config.PLAYER_SEARCH_TOP_RATIO))
        cut = max(0, min(height - 1, cut))
        mask[:cut, :] = 0
        return clean_mask(mask, config.MORPH_KERNEL)

    # ------------------------------------------------------------------
    def _candidates(
        self, frame: np.ndarray, mask: np.ndarray
    ) -> List[Tuple[int, int, int, int, float]]:
        height, width = frame.shape[:2]
        frame_area = float(height * width)
        min_area = frame_area * float(config.PLAYER_MIN_AREA_RATIO)
        max_area = frame_area * float(config.PLAYER_MAX_AREA_RATIO)

        gap = int(width * float(config.OBSTACLE_MERGE_DISTANCE_RATIO) * 0.5)
        boxes = merge_boxes(find_boxes(mask), gap=max(1, gap))

        good: List[Tuple[int, int, int, int, float]] = []
        for x, y, w, h, area in boxes:
            box_area = float(w * h)
            if box_area < min_area or box_area > max_area:
                continue
            if w <= 1 or h <= 1:
                continue
            aspect = max(w / float(h), h / float(w))
            if aspect > float(config.PLAYER_MAX_ASPECT):
                continue
            good.append((x, y, w, h, area))
        return good

    # ------------------------------------------------------------------
    def detect(self, frame: np.ndarray) -> Optional[DetectedObject]:
        """
        Найти игрока. Возвращает DetectedObject (x, y, width, height,
        center_x, center_y) либо последнее известное положение, либо None.
        """
        if frame is None or frame.size == 0:
            return self.last_player

        mask = self.build_mask(frame)
        self.last_mask = mask
        candidates = self._candidates(frame, mask)

        height, width = frame.shape[:2]
        expected_x = (
            self.last_player.center_x
            if self.last_player is not None
            else width * float(config.CENTER_X_RATIO)
        )

        best: Optional[Tuple[int, int, int, int, float]] = None
        best_score = -1e18
        for x, y, w, h, area in candidates:
            cx = x + w / 2.0
            cy = y + h / 2.0
            # Чем ниже объект и чем ближе он к ожидаемой позиции — тем лучше.
            score = 0.0
            score += (cy / float(height)) * 900.0          # низ экрана — плюс
            score += (area / float(width * height)) * 4000.0
            score -= abs(cx - expected_x) / float(width) * 700.0
            if score > best_score:
                best_score = score
                best = (x, y, w, h, area)

        if best is None:
            self.detected_this_frame = False
            self.frames_since_seen += 1
            if self.frames_since_seen > int(config.PLAYER_MEMORY_FRAMES):
                return None
            return self.last_player

        x, y, w, h, area = best
        player = DetectedObject(
            x=int(x),
            y=int(y),
            width=int(w),
            height=int(h),
            area=float(area),
            kind="player",
            confidence=1.0,
        )
        self.detected_this_frame = True
        self.frames_since_seen = 0
        self.last_player = player
        return player

    # ------------------------------------------------------------------
    def fallback_player(self, frame: np.ndarray) -> DetectedObject:
        """
        Аварийная оценка позиции игрока, когда детекция невозможна:
        центр по горизонтали, нижняя четверть по вертикали.
        """
        height, width = frame.shape[:2]
        w = int(config.PLAYER_WIDTH)
        h = int(config.PLAYER_WIDTH)
        cx = int(width * float(config.CENTER_X_RATIO))
        cy = int(height * 0.85)
        return DetectedObject(
            x=cx - w // 2,
            y=cy - h // 2,
            width=w,
            height=h,
            area=float(w * h),
            kind="player_guess",
            confidence=0.2,
        )

    # ------------------------------------------------------------------
    def is_lost(self) -> bool:
        return self.frames_since_seen > int(config.PLAYER_MEMORY_FRAMES)


# Глобальный детектор для функционального интерфейса detect_player(frame)
_DEFAULT_DETECTOR = PlayerDetector()


def detect_player(frame: np.ndarray) -> Optional[DetectedObject]:
    """
    Функциональный интерфейс (как в ТЗ): detect_player(frame).
    Возвращает объект с полями x, y, width, height, center_x, center_y.
    """
    return _DEFAULT_DETECTOR.detect(frame)


def reset_player_detector() -> None:
    _DEFAULT_DETECTOR.reset()


def draw_player(frame: np.ndarray, player: Optional[DetectedObject]) -> np.ndarray:
    """Нарисовать игрока (используется debug_view)."""
    if player is None:
        return frame
    cv2.rectangle(
        frame,
        (player.left, player.top),
        (player.right, player.bottom),
        (0, 255, 0),
        2,
    )
    cv2.circle(frame, (player.center_x, player.center_y), 3, (0, 255, 0), -1)
    return frame

"""
obstacle_detector.py — поиск препятствий на кадре.

Препятствия ищутся по ярким цветам (config.OBSTACLE_HSV_*), а также
по яркому контрасту относительно тёмного фона. Зелёные линии дорожки
(config.TRACK_HSV_*) вычитаются из маски, чтобы не путать их с препятствиями.
"""

from __future__ import annotations

from typing import List, Optional

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


class ObstacleDetector:
    """Детектор препятствий."""

    def __init__(self) -> None:
        self.last_mask: Optional[np.ndarray] = None
        self.last_obstacles: List[DetectedObject] = []

    # ------------------------------------------------------------------
    def build_mask(self, frame: np.ndarray) -> np.ndarray:
        hsv = to_hsv(frame)
        mask = color_mask(
            hsv,
            config.OBSTACLE_HSV_LOWER,
            config.OBSTACLE_HSV_UPPER,
            config.OBSTACLE_HSV_EXTRA_RANGES,
        )

        if config.USE_TRACK_MASK:
            track = color_mask(hsv, config.TRACK_HSV_LOWER, config.TRACK_HSV_UPPER)
            track = cv2.dilate(
                track, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)), iterations=1
            )
            mask = cv2.bitwise_and(mask, cv2.bitwise_not(track))

        # Ниже игрока препятствия уже не важны (они позади).
        height = frame.shape[0]
        cut = int(height * float(config.OBSTACLE_SEARCH_BOTTOM_RATIO))
        cut = max(1, min(height, cut))
        mask[cut:, :] = 0

        return clean_mask(mask, config.MORPH_KERNEL)

    # ------------------------------------------------------------------
    def detect(
        self, frame: np.ndarray, player: Optional[DetectedObject] = None
    ) -> List[DetectedObject]:
        """
        Найти препятствия. Каждый объект содержит x, y, width, height,
        center_x, center_y.
        """
        if frame is None or frame.size == 0:
            return []

        mask = self.build_mask(frame)
        self.last_mask = mask

        height, width = frame.shape[:2]
        frame_area = float(height * width)
        min_area = frame_area * float(config.OBSTACLE_MIN_AREA_RATIO)
        max_area = frame_area * float(config.OBSTACLE_MAX_AREA_RATIO)
        min_w = width * float(config.OBSTACLE_MIN_WIDTH_RATIO)
        min_h = height * float(config.OBSTACLE_MIN_HEIGHT_RATIO)
        gap = max(2, int(width * float(config.OBSTACLE_MERGE_DISTANCE_RATIO)))

        boxes = merge_boxes(find_boxes(mask), gap=gap)

        obstacles: List[DetectedObject] = []
        for x, y, w, h, area in boxes:
            box_area = float(w * h)
            if box_area < min_area or box_area > max_area:
                continue
            if w < min_w and h < min_h:
                continue
            # Не считать препятствием сам корпус игрока
            if player is not None and self._overlaps_player(x, y, w, h, player):
                continue
            obstacle = DetectedObject(
                x=int(x),
                y=int(y),
                width=int(w),
                height=int(h),
                area=float(area),
                kind="obstacle",
                confidence=float(min(1.0, area / max(1.0, box_area))),
            )
            obstacles.append(obstacle)

        # Ближайшие к игроку (нижние) — первыми
        obstacles.sort(key=lambda o: o.bottom, reverse=True)
        self.last_obstacles = obstacles
        return obstacles

    # ------------------------------------------------------------------
    @staticmethod
    def _overlaps_player(
        x: int, y: int, w: int, h: int, player: DetectedObject
    ) -> bool:
        inter_x = min(x + w, player.right) - max(x, player.left)
        inter_y = min(y + h, player.bottom) - max(y, player.top)
        if inter_x <= 0 or inter_y <= 0:
            return False
        inter = inter_x * inter_y
        return inter > 0.55 * float(max(1, w * h))

    # ------------------------------------------------------------------
    def obstacles_ahead(
        self, obstacles: List[DetectedObject], player: Optional[DetectedObject]
    ) -> List[DetectedObject]:
        """Только препятствия, находящиеся ПЕРЕД персонажем (выше него)."""
        if player is None:
            return obstacles
        return [o for o in obstacles if o.bottom < player.bottom]


# Глобальный детектор для функционального интерфейса
_DEFAULT_DETECTOR = ObstacleDetector()


def detect_obstacles(
    frame: np.ndarray, player: Optional[DetectedObject] = None
) -> List[DetectedObject]:
    """Функциональный интерфейс (как в ТЗ): detect_obstacles(frame)."""
    return _DEFAULT_DETECTOR.detect(frame, player)


def draw_obstacles(frame: np.ndarray, obstacles: List[DetectedObject]) -> np.ndarray:
    for obstacle in obstacles:
        cv2.rectangle(
            frame,
            (obstacle.left, obstacle.top),
            (obstacle.right, obstacle.bottom),
            (0, 0, 255),
            2,
        )
    return frame

"""
tracker.py — трекинг препятствий между кадрами и прогноз их движения.

Задачи модуля:
  * сопоставить препятствия текущего кадра с предыдущими;
  * оценить скорость каждого препятствия (px/сек);
  * спрогнозировать положение через config.PREDICTION_TIME секунд;
  * посчитать distance_x / distance_y / relative_position до игрока;
  * оценить время до столкновения (time_to_impact).
"""

from __future__ import annotations

import math
import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

import config
from vision_utils import DetectedObject


@dataclass
class TrackedObstacle:
    """Препятствие с историей и оценкой скорости."""

    track_id: int
    box: DetectedObject
    speed_x: float = 0.0            # px/сек
    speed_y: float = 0.0            # px/сек (положительное = движется вниз)
    last_seen: float = field(default_factory=time.time)
    age: int = 1
    misses: int = 0
    history: List[Tuple[float, int, int]] = field(default_factory=list)

    # Вычисляется относительно игрока в update()
    distance_x: float = 0.0
    distance_y: float = 0.0
    relative_position: str = "unknown"   # left / right / center
    time_to_impact: float = float("inf")
    predicted_center_x: int = 0
    predicted_center_y: int = 0

    @property
    def center_x(self) -> int:
        return self.box.center_x

    @property
    def center_y(self) -> int:
        return self.box.center_y

    @property
    def width(self) -> int:
        return self.box.width

    @property
    def height(self) -> int:
        return self.box.height

    @property
    def left(self) -> int:
        return self.box.left

    @property
    def right(self) -> int:
        return self.box.right

    @property
    def top(self) -> int:
        return self.box.top

    @property
    def bottom(self) -> int:
        return self.box.bottom

    @property
    def approaching(self) -> bool:
        return self.speed_y > float(config.MIN_SPEED_FOR_PREDICTION)


class ObstacleTracker:
    """Простой трекер по ближайшему соседу (nearest-neighbour)."""

    def __init__(self) -> None:
        self.tracks: Dict[int, TrackedObstacle] = {}
        self._next_id = 1
        self._last_time: Optional[float] = None
        self.average_speed_y: float = 0.0

    # ------------------------------------------------------------------
    def reset(self) -> None:
        self.tracks.clear()
        self._next_id = 1
        self._last_time = None
        self.average_speed_y = 0.0

    # ------------------------------------------------------------------
    def update(
        self,
        obstacles: List[DetectedObject],
        player: Optional[DetectedObject],
        frame_shape: Tuple[int, int],
        now: Optional[float] = None,
    ) -> List[TrackedObstacle]:
        """
        Обновить треки новыми детекциями.
        frame_shape = (height, width).
        Возвращает актуальный список треков.
        """
        now = time.time() if now is None else now
        dt = 0.0 if self._last_time is None else max(1e-3, now - self._last_time)
        self._last_time = now

        height, width = frame_shape[0], frame_shape[1]
        match_radius = float(width) * float(config.TRACK_MATCH_DISTANCE_RATIO)

        unmatched_tracks = dict(self.tracks)
        matched_ids: List[int] = []

        for detection in obstacles:
            best_id: Optional[int] = None
            best_dist = match_radius
            for track_id, track in unmatched_tracks.items():
                dx = detection.center_x - track.center_x
                dy = detection.center_y - track.center_y
                # Препятствия движутся сверху вниз: назад (вверх) — маловероятно.
                if dy < -match_radius * 0.5:
                    continue
                dist = math.hypot(dx, dy * 0.7)
                width_ratio = detection.width / max(1.0, float(track.width))
                if width_ratio < 0.35 or width_ratio > 3.0:
                    dist *= 1.6
                if dist < best_dist:
                    best_dist = dist
                    best_id = track_id

            if best_id is None:
                track = TrackedObstacle(
                    track_id=self._next_id,
                    box=detection,
                    history=[(now, detection.center_x, detection.center_y)],
                )
                self.tracks[self._next_id] = track
                matched_ids.append(self._next_id)
                self._next_id += 1
            else:
                track = unmatched_tracks.pop(best_id)
                self._update_track(track, detection, now, dt)
                matched_ids.append(best_id)

        # Треки без детекции в этом кадре
        for track_id, track in unmatched_tracks.items():
            track.misses += 1

        # Удаляем устаревшие
        for track_id in [
            tid
            for tid, tr in self.tracks.items()
            if tr.misses > int(config.TRACK_MAX_MISSES)
        ]:
            self.tracks.pop(track_id, None)

        self._compute_relations(player, height, width)
        self._update_average_speed()
        return self.active_tracks()

    # ------------------------------------------------------------------
    def _update_track(
        self,
        track: TrackedObstacle,
        detection: DetectedObject,
        now: float,
        dt: float,
    ) -> None:
        prev_cx, prev_cy = track.center_x, track.center_y
        elapsed = max(1e-3, now - track.last_seen)

        raw_speed_x = (detection.center_x - prev_cx) / elapsed
        raw_speed_y = (detection.center_y - prev_cy) / elapsed

        alpha = float(config.TRACK_SPEED_SMOOTHING)
        alpha = min(0.95, max(0.0, alpha))
        if track.age <= 1:
            track.speed_x = raw_speed_x
            track.speed_y = raw_speed_y
        else:
            track.speed_x = alpha * track.speed_x + (1.0 - alpha) * raw_speed_x
            track.speed_y = alpha * track.speed_y + (1.0 - alpha) * raw_speed_y

        track.box = detection
        track.last_seen = now
        track.age += 1
        track.misses = 0
        track.history.append((now, detection.center_x, detection.center_y))
        if len(track.history) > 30:
            track.history = track.history[-30:]

    # ------------------------------------------------------------------
    def _compute_relations(
        self, player: Optional[DetectedObject], height: int, width: int
    ) -> None:
        if player is None:
            return
        horizon = float(config.PREDICTION_TIME)
        tolerance = float(config.LANE_TOLERANCE)

        for track in self.tracks.values():
            track.distance_x = float(track.center_x - player.center_x)
            track.distance_y = float(player.top - track.bottom)

            if track.center_x < player.center_x - tolerance:
                track.relative_position = "left"
            elif track.center_x > player.center_x + tolerance:
                track.relative_position = "right"
            else:
                track.relative_position = "center"

            track.predicted_center_x = int(track.center_x + track.speed_x * horizon)
            track.predicted_center_y = int(track.center_y + track.speed_y * horizon)

            if track.speed_y > float(config.MIN_SPEED_FOR_PREDICTION):
                track.time_to_impact = max(0.0, track.distance_y / track.speed_y)
            else:
                track.time_to_impact = float("inf")

    # ------------------------------------------------------------------
    def _update_average_speed(self) -> None:
        speeds = [
            t.speed_y
            for t in self.tracks.values()
            if t.age > 2 and t.speed_y > float(config.MIN_SPEED_FOR_PREDICTION)
        ]
        if speeds:
            self.average_speed_y = sum(speeds) / len(speeds)

    # ------------------------------------------------------------------
    def active_tracks(self) -> List[TrackedObstacle]:
        return sorted(
            (t for t in self.tracks.values() if t.misses == 0),
            key=lambda t: t.bottom,
            reverse=True,
        )

    # ------------------------------------------------------------------
    def nearest_ahead(
        self, player: Optional[DetectedObject]
    ) -> Optional[TrackedObstacle]:
        """Ближайшее препятствие ПЕРЕД игроком."""
        if player is None:
            return None
        ahead = [t for t in self.active_tracks() if t.bottom <= player.bottom]
        if not ahead:
            return None
        return min(ahead, key=lambda t: player.top - t.bottom if player.top > t.bottom else 0)

    # ------------------------------------------------------------------
    def threatening(
        self, player: Optional[DetectedObject], reaction_distance: float
    ) -> List[TrackedObstacle]:
        """
        Препятствия, которые находятся впереди и попадают в траекторию
        игрока (с учётом прогноза движения).
        """
        if player is None:
            return []
        margin = float(config.OBSTACLE_MARGIN)
        half_player = max(6.0, player.width / 2.0)
        result: List[TrackedObstacle] = []
        for track in self.active_tracks():
            if track.bottom > player.bottom:
                continue
            if track.distance_y > reaction_distance:
                continue
            predicted_left = track.left + (track.predicted_center_x - track.center_x)
            predicted_right = track.right + (track.predicted_center_x - track.center_x)
            if (
                predicted_right + margin >= player.center_x - half_player
                and predicted_left - margin <= player.center_x + half_player
            ):
                result.append(track)
        result.sort(key=lambda t: t.distance_y)
        return result

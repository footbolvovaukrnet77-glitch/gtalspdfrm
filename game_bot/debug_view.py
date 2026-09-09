"""
debug_view.py — отладочное окно OpenCV.

Рисует:
  * зелёный прямоугольник вокруг игрока;
  * красные прямоугольники вокруг препятствий (жёлтый — ближайшая угроза);
  * вертикальную линию центра игрока;
  * центральную траекторию кадра;
  * предполагаемую траекторию (куда бот ведёт персонажа);
  * безопасные зоны слева/справа (полупрозрачные);
  * текст: состояние, FPS, дистанция, действие, координаты.
"""

from __future__ import annotations

from typing import List, Optional

import cv2
import numpy as np

import config
from decision_engine import Decision
from tracker import TrackedObstacle
from vision_utils import DetectedObject

COLOR_PLAYER = (0, 255, 0)
COLOR_OBSTACLE = (0, 0, 255)
COLOR_THREAT = (0, 215, 255)
COLOR_CENTER = (200, 200, 200)
COLOR_TRAJECTORY = (255, 200, 0)
COLOR_SAFE = (0, 180, 0)
COLOR_TEXT = (255, 255, 255)
COLOR_TEXT_BG = (0, 0, 0)

FONT = cv2.FONT_HERSHEY_SIMPLEX


class DebugView:
    """Окно отладки. Создаётся лениво, закрывается через close()."""

    def __init__(self, window_name: Optional[str] = None) -> None:
        self.window_name = window_name or config.DEBUG_WINDOW_NAME
        self._created = False

    # ------------------------------------------------------------------
    def render(
        self,
        frame: np.ndarray,
        player: Optional[DetectedObject],
        tracks: List[TrackedObstacle],
        decision: Decision,
        fps: float,
        extra_lines: Optional[List[str]] = None,
    ) -> np.ndarray:
        """Собрать отладочный кадр (BGR)."""
        canvas = frame.copy()
        height, width = canvas.shape[:2]

        self._draw_safe_zones(canvas, decision, player, height)
        self._draw_center_line(canvas, width, height)
        self._draw_obstacles(canvas, tracks, decision)
        self._draw_player(canvas, player, height)
        self._draw_trajectory(canvas, player, decision, height)
        self._draw_hud(canvas, player, decision, fps, extra_lines)
        return canvas

    # ------------------------------------------------------------------
    def _draw_safe_zones(
        self,
        canvas: np.ndarray,
        decision: Decision,
        player: Optional[DetectedObject],
        height: int,
    ) -> None:
        if player is None:
            return
        overlay = canvas.copy()
        top = max(0, int(player.top - float(config.REACTION_DISTANCE)))
        bottom = int(player.bottom)

        for zone, ok in (
            (decision.left_zone, decision.free_left > 0),
            (decision.right_zone, decision.free_right > 0),
        ):
            x1, x2 = int(zone[0]), int(zone[1])
            if x2 - x1 < 2:
                continue
            color = COLOR_SAFE if ok else (60, 60, 160)
            cv2.rectangle(overlay, (x1, top), (x2, bottom), color, -1)
        cv2.addWeighted(overlay, 0.18, canvas, 0.82, 0, canvas)

        for zone in (decision.left_zone, decision.right_zone):
            x1, x2 = int(zone[0]), int(zone[1])
            if x2 - x1 < 2:
                continue
            cv2.rectangle(canvas, (x1, top), (x2, bottom), COLOR_SAFE, 1)

    # ------------------------------------------------------------------
    def _draw_center_line(self, canvas: np.ndarray, width: int, height: int) -> None:
        center = int(width * float(config.CENTER_X_RATIO))
        cv2.line(canvas, (center, 0), (center, height), COLOR_CENTER, 1)
        tolerance = int(config.LANE_TOLERANCE)
        cv2.line(canvas, (center - tolerance, 0), (center - tolerance, height), (90, 90, 90), 1)
        cv2.line(canvas, (center + tolerance, 0), (center + tolerance, height), (90, 90, 90), 1)

    # ------------------------------------------------------------------
    def _draw_obstacles(
        self, canvas: np.ndarray, tracks: List[TrackedObstacle], decision: Decision
    ) -> None:
        threat_id = decision.threat.track_id if decision.threat is not None else None
        for track in tracks:
            color = COLOR_THREAT if track.track_id == threat_id else COLOR_OBSTACLE
            cv2.rectangle(
                canvas, (track.left, track.top), (track.right, track.bottom), color, 2
            )
            label = f"#{track.track_id} vy={track.speed_y:.0f}"
            cv2.putText(
                canvas, label, (track.left, max(12, track.top - 5)), FONT, 0.4, color, 1
            )
            # Прогноз положения препятствия
            if abs(track.speed_y) > float(config.MIN_SPEED_FOR_PREDICTION):
                cv2.arrowedLine(
                    canvas,
                    (track.center_x, track.center_y),
                    (track.predicted_center_x, track.predicted_center_y),
                    color,
                    1,
                    tipLength=0.3,
                )

    # ------------------------------------------------------------------
    def _draw_player(
        self, canvas: np.ndarray, player: Optional[DetectedObject], height: int
    ) -> None:
        if player is None:
            return
        cv2.rectangle(
            canvas, (player.left, player.top), (player.right, player.bottom), COLOR_PLAYER, 2
        )
        cv2.line(
            canvas, (player.center_x, 0), (player.center_x, height), COLOR_PLAYER, 1
        )
        cv2.circle(canvas, (player.center_x, player.center_y), 3, COLOR_PLAYER, -1)

    # ------------------------------------------------------------------
    def _draw_trajectory(
        self,
        canvas: np.ndarray,
        player: Optional[DetectedObject],
        decision: Decision,
        height: int,
    ) -> None:
        if player is None:
            return
        target_x = decision.target_x
        if target_x is None:
            target_x = player.center_x
        top_y = max(0, int(player.top - float(config.REACTION_DISTANCE)))
        cv2.arrowedLine(
            canvas,
            (player.center_x, player.top),
            (int(target_x), top_y),
            COLOR_TRAJECTORY,
            2,
            tipLength=0.08,
        )
        if decision.threat is not None:
            cv2.line(
                canvas,
                (player.center_x, player.top),
                (decision.threat.center_x, decision.threat.bottom),
                (0, 140, 255),
                1,
            )

    # ------------------------------------------------------------------
    def _draw_hud(
        self,
        canvas: np.ndarray,
        player: Optional[DetectedObject],
        decision: Decision,
        fps: float,
        extra_lines: Optional[List[str]],
    ) -> None:
        lines: List[str] = []
        if player is not None:
            lines.append(f"PLAYER: x={player.center_x} y={player.center_y} w={player.width}")
        else:
            lines.append("PLAYER: NOT FOUND")

        if decision.threat is not None:
            threat = decision.threat
            lines.append(f"OBSTACLE: x={threat.center_x} y={threat.center_y} w={threat.width}")
            lines.append(f"DISTANCE: {decision.distance:.0f}")
            tti = decision.time_to_impact
            tti_text = "inf" if tti == float("inf") else f"{tti:.2f}s"
            lines.append(f"TTI: {tti_text}  SPEED: {decision.speed:.0f}px/s")
        else:
            lines.append("OBSTACLE: none")
            lines.append("DISTANCE: -")

        lines.append(f"FREE L/R: {decision.free_left:.0f} / {decision.free_right:.0f}")
        lines.append(f"ACTION: {decision.action}")
        lines.append(f"STATE: {decision.state}")
        lines.append(f"FPS: {fps:.1f}")
        if decision.reason:
            lines.append(f"WHY: {decision.reason}")
        for line in extra_lines or []:
            lines.append(line)

        box_h = 18 * len(lines) + 10
        box_w = 340
        overlay = canvas.copy()
        cv2.rectangle(overlay, (5, 5), (5 + box_w, 5 + box_h), COLOR_TEXT_BG, -1)
        cv2.addWeighted(overlay, 0.55, canvas, 0.45, 0, canvas)

        y = 24
        for line in lines:
            cv2.putText(canvas, line, (12, y), FONT, 0.45, COLOR_TEXT, 1, cv2.LINE_AA)
            y += 18

    # ------------------------------------------------------------------
    def show(self, canvas: np.ndarray) -> int:
        """Показать кадр. Возвращает код нажатой клавиши (waitKey)."""
        if not self._created:
            cv2.namedWindow(self.window_name, cv2.WINDOW_NORMAL)
            self._created = True
        cv2.imshow(self.window_name, canvas)
        return cv2.waitKey(1) & 0xFF

    # ------------------------------------------------------------------
    def show_masks(
        self, player_mask: Optional[np.ndarray], obstacle_mask: Optional[np.ndarray]
    ) -> None:
        if player_mask is not None:
            cv2.imshow("Mask: player", player_mask)
        if obstacle_mask is not None:
            cv2.imshow("Mask: obstacles", obstacle_mask)

    # ------------------------------------------------------------------
    def draw_game_over(self, canvas: np.ndarray, reason: str = "") -> np.ndarray:
        height, width = canvas.shape[:2]
        overlay = canvas.copy()
        cv2.rectangle(overlay, (0, 0), (width, height), (0, 0, 0), -1)
        cv2.addWeighted(overlay, 0.55, canvas, 0.45, 0, canvas)
        cv2.putText(
            canvas,
            "GAME OVER",
            (int(width * 0.16), int(height * 0.45)),
            FONT,
            1.6,
            (0, 0, 255),
            3,
            cv2.LINE_AA,
        )
        cv2.putText(
            canvas,
            "[R] restart   [ESC] stop",
            (int(width * 0.16), int(height * 0.55)),
            FONT,
            0.7,
            COLOR_TEXT,
            2,
            cv2.LINE_AA,
        )
        if reason:
            cv2.putText(
                canvas,
                reason,
                (int(width * 0.16), int(height * 0.62)),
                FONT,
                0.5,
                (200, 200, 200),
                1,
                cv2.LINE_AA,
            )
        return canvas

    # ------------------------------------------------------------------
    def close(self) -> None:
        try:
            cv2.destroyAllWindows()
        except Exception:  # pragma: no cover
            pass
        self._created = False

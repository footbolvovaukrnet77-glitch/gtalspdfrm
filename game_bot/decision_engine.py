"""
decision_engine.py — логика принятия решений (уклонение / прыжок / центр).

Алгоритм на каждом кадре:
  1. Находим препятствия, которые ПЕРЕД игроком и попадают в его траекторию
     (с учётом прогноза их движения — tracker).
  2. Если угрозы нет — держимся центральной траектории.
  3. Если угроза есть:
       а) считаем свободное место слева и справа (с учётом других препятствий
          и краёв кадра);
       б) выбираем сторону с бОльшим запасом;
       в) если обойти сбоку нельзя — прыгаем (заранее, а не в последний момент).
  4. После прохождения препятствия — RECOVERING -> MOVING_CENTER.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

import config
from game_state import State, StateMachine
from tracker import ObstacleTracker, TrackedObstacle
from vision_utils import DetectedObject


# Действия, которые decision_engine возвращает исполнителю
ACTION_NONE = "NONE"
ACTION_LEFT = "MOVE LEFT"
ACTION_RIGHT = "MOVE RIGHT"
ACTION_JUMP = "JUMP"
ACTION_CENTER_LEFT = "CENTER LEFT"
ACTION_CENTER_RIGHT = "CENTER RIGHT"
ACTION_HOLD = "HOLD"


@dataclass
class Decision:
    """Результат работы движка решений."""

    action: str = ACTION_NONE
    state: State = State.SEARCHING
    reason: str = ""
    threat: Optional[TrackedObstacle] = None
    distance: float = float("inf")
    free_left: float = 0.0
    free_right: float = 0.0
    left_zone: Tuple[int, int] = (0, 0)     # (x1, x2) безопасной зоны слева
    right_zone: Tuple[int, int] = (0, 0)    # (x1, x2) безопасной зоны справа
    target_x: Optional[int] = None
    speed: float = 0.0
    time_to_impact: float = float("inf")
    notes: List[str] = field(default_factory=list)


class DecisionEngine:
    """Принимает решения и управляет переходами конечного автомата."""

    def __init__(self, state_machine: Optional[StateMachine] = None) -> None:
        self.sm = state_machine or StateMachine()
        self.last_decision = Decision()
        self._avoid_started: float = 0.0
        self._avoid_side: str = ""
        self._recover_until: float = 0.0
        self._last_jump_request: float = 0.0
        self._jumped_tracks: Dict[int, float] = {}

    # ------------------------------------------------------------------
    def reset(self) -> None:
        self.sm.reset(State.SEARCHING)
        self.last_decision = Decision()
        self._avoid_started = 0.0
        self._avoid_side = ""
        self._recover_until = 0.0
        self._jumped_tracks.clear()

    # ------------------------------------------------------------------
    def center_x(self, frame_width: int) -> int:
        return int(frame_width * float(config.CENTER_X_RATIO))

    # ------------------------------------------------------------------
    def decide(
        self,
        player: Optional[DetectedObject],
        tracker: ObstacleTracker,
        frame_shape: Tuple[int, int],
        now: Optional[float] = None,
    ) -> Decision:
        """Главная функция: вернуть решение для текущего кадра."""
        now = time.time() if now is None else now
        height, width = int(frame_shape[0]), int(frame_shape[1])

        decision = Decision(state=self.sm.state)

        if player is None:
            self.sm.transition(State.SEARCHING)
            decision.state = self.sm.state
            decision.action = ACTION_NONE
            decision.reason = "игрок не найден"
            self.last_decision = decision
            return decision

        threats = tracker.threatening(player, float(config.REACTION_DISTANCE))
        band = self._band_obstacles(tracker, player)
        decision.speed = tracker.average_speed_y

        if not threats:
            self._resolve_no_threat(decision, player, width, now, band)
            decision.free_left, decision.free_right, decision.left_zone, decision.right_zone = (
                self._free_space(player, band, None, width)
            )
            self.last_decision = decision
            return decision

        threat = threats[0]
        decision.threat = threat
        decision.distance = max(0.0, threat.distance_y)
        decision.time_to_impact = threat.time_to_impact

        free_left, free_right, left_zone, right_zone = self._free_space(
            player, band, threat, width
        )
        decision.free_left = free_left
        decision.free_right = free_right
        decision.left_zone = left_zone
        decision.right_zone = right_zone

        jumpable = threat.height <= height * float(config.JUMPABLE_HEIGHT_RATIO)
        too_wide = threat.width >= width * float(config.UNAVOIDABLE_WIDTH_RATIO)
        needed = max(2.0, player.width / 2.0)

        side_possible_left = free_left >= needed
        side_possible_right = free_right >= needed

        # --- Прыжок: заранее, если сбоку не обойти -----------------------
        jump_window = self._jump_window(decision)
        must_jump = (not side_possible_left and not side_possible_right) or too_wide
        if must_jump and jumpable and jump_window:
            if self._already_jumped(threat, now):
                # Прыжок на это препятствие уже выполнен — держим позицию,
                # чтобы не спамить SPACE.
                self.sm.transition(State.JUMPING)
                decision.state = self.sm.state
                decision.action = ACTION_HOLD
                decision.reason = "прыжок уже выполнен"
                self.last_decision = decision
                return decision
            self.sm.transition(State.JUMPING)
            decision.state = self.sm.state
            decision.action = ACTION_JUMP
            decision.reason = (
                "сбоку не обойти" if not too_wide else "препятствие слишком широкое"
            )
            decision.notes.append(f"height={threat.height} <= jumpable")
            self._mark_jumped(threat, now)
            self._recover_until = now + 0.35
            self.last_decision = decision
            return decision

        # --- Обход сбоку -------------------------------------------------
        if decision.distance <= float(config.REACTION_DISTANCE):
            side = self._choose_side(
                player, threat, free_left, free_right, side_possible_left, side_possible_right, width
            )
            if side == "left":
                self.sm.transition(State.AVOID_LEFT)
                decision.state = self.sm.state
                decision.action = ACTION_LEFT
                decision.target_x = int(
                    threat.left - float(config.OBSTACLE_MARGIN) - player.width / 2.0
                )
                decision.reason = f"свободнее слева ({free_left:.0f} > {free_right:.0f})"
                self._avoid_side = "left"
                self._avoid_started = now
            elif side == "right":
                self.sm.transition(State.AVOID_RIGHT)
                decision.state = self.sm.state
                decision.action = ACTION_RIGHT
                decision.target_x = int(
                    threat.right + float(config.OBSTACLE_MARGIN) + player.width / 2.0
                )
                decision.reason = f"свободнее справа ({free_right:.0f} > {free_left:.0f})"
                self._avoid_side = "right"
                self._avoid_started = now
            else:
                # Совсем некуда — пробуем прыжок как последний шанс
                if jump_window and not self._already_jumped(threat, now):
                    self.sm.transition(State.JUMPING)
                    decision.state = self.sm.state
                    decision.action = ACTION_JUMP
                    decision.reason = "нет безопасной стороны — прыжок"
                    self._mark_jumped(threat, now)
                    self._recover_until = now + 0.35
                else:
                    decision.action = ACTION_HOLD
                    decision.reason = "жду сближения"
            self.last_decision = decision
            return decision

        # Препятствие ещё далеко — держим центр
        self._resolve_no_threat(decision, player, width, now, band)
        self.last_decision = decision
        return decision

    # ------------------------------------------------------------------
    def _resolve_no_threat(
        self,
        decision: Decision,
        player: DetectedObject,
        width: int,
        now: float,
        band: Optional[List[TrackedObstacle]] = None,
    ) -> None:
        """Нет угрозы: RECOVERING -> MOVING_CENTER -> SEARCHING."""
        center = self.center_x(width)
        offset = player.center_x - center
        tolerance = float(config.LANE_TOLERANCE)

        if self.sm.state in (State.AVOID_LEFT, State.AVOID_RIGHT, State.JUMPING):
            self.sm.transition(State.RECOVERING)
            self._recover_until = max(self._recover_until, now + 0.2)

        if self.sm.state == State.RECOVERING and now < self._recover_until:
            decision.state = self.sm.state
            decision.action = ACTION_HOLD
            decision.reason = "стабилизация после манёвра"
            decision.target_x = center
            return

        # Возвращаться к центру можно только после того, как препятствие
        # действительно прошло: иначе бот сам въедет обратно в него.
        if abs(offset) > tolerance and self._path_blocked(player, band, center):
            self.sm.transition(State.RECOVERING)
            decision.state = self.sm.state
            decision.action = ACTION_HOLD
            decision.reason = "жду, пока препятствие пройдёт"
            decision.target_x = player.center_x
            return

        if abs(offset) > tolerance:
            self.sm.transition(State.MOVING_CENTER)
            decision.state = self.sm.state
            decision.target_x = center
            if offset > 0:
                decision.action = ACTION_CENTER_LEFT
                decision.reason = f"возврат к центру (смещение {offset:.0f}px вправо)"
            else:
                decision.action = ACTION_CENTER_RIGHT
                decision.reason = f"возврат к центру (смещение {abs(offset):.0f}px влево)"
            return

        self.sm.transition(State.SEARCHING)
        decision.state = self.sm.state
        decision.action = ACTION_NONE
        decision.reason = "путь свободен"
        decision.target_x = center

    # ------------------------------------------------------------------
    def _jump_window(self, decision: Decision) -> bool:
        """
        Пора ли прыгать. Если скорость препятствия известна — ориентируемся
        на время до столкновения (JUMP_TIME_AHEAD), иначе — на расстояние.
        Прыжок всегда происходит заранее, но не дальше JUMP_DISTANCE.
        """
        if decision.distance > float(config.JUMP_DISTANCE):
            return False
        tti = decision.time_to_impact
        if tti == float("inf"):
            return True
        return tti <= float(config.JUMP_TIME_AHEAD)

    # ------------------------------------------------------------------
    def _already_jumped(self, track: TrackedObstacle, now: float) -> bool:
        """Прыгали ли мы недавно на это же препятствие (защита от спама SPACE)."""
        timestamp = self._jumped_tracks.get(track.track_id)
        if timestamp is None:
            return False
        return (now - timestamp) < float(config.JUMP_SAME_OBSTACLE_COOLDOWN)

    def _mark_jumped(self, track: Optional[TrackedObstacle], now: float) -> None:
        if track is None:
            return
        self._jumped_tracks[track.track_id] = now
        # Чистим старые записи, чтобы словарь не рос бесконечно
        ttl = float(config.JUMP_SAME_OBSTACLE_COOLDOWN) * 4.0
        for track_id in [t for t, ts in self._jumped_tracks.items() if now - ts > ttl]:
            self._jumped_tracks.pop(track_id, None)

    # ------------------------------------------------------------------
    def _path_blocked(
        self,
        player: DetectedObject,
        band: Optional[List[TrackedObstacle]],
        target_x: int,
    ) -> bool:
        """
        Пересекает ли путь от текущей позиции к target_x какое-либо
        препятствие, которое ещё не прошло игрока.
        """
        if not band:
            return False
        margin = float(config.OBSTACLE_MARGIN)
        half_player = max(4.0, player.width / 2.0)
        lo = min(player.center_x, target_x) - half_player - margin
        hi = max(player.center_x, target_x) + half_player + margin
        for track in band:
            if track.top >= player.bottom:
                continue  # препятствие уже полностью позади игрока
            if track.right >= lo and track.left <= hi:
                return True
        return False

    # ------------------------------------------------------------------
    def _band_obstacles(
        self, tracker: ObstacleTracker, player: DetectedObject
    ) -> List[TrackedObstacle]:
        """Препятствия в «опасной полосе» перед игроком."""
        reaction = float(config.REACTION_DISTANCE)
        band: List[TrackedObstacle] = []
        for track in tracker.active_tracks():
            # Препятствие, поравнявшееся с игроком, всё ещё опасно:
            # исключаем только то, что полностью позади него.
            if track.top >= player.bottom:
                continue
            if track.distance_y > reaction:
                continue
            band.append(track)
        return band

    # ------------------------------------------------------------------
    def _free_space(
        self,
        player: DetectedObject,
        band: List[TrackedObstacle],
        threat: Optional[TrackedObstacle],
        width: int,
    ) -> Tuple[float, float, Tuple[int, int], Tuple[int, int]]:
        """
        Сколько свободного места слева и справа от угрозы.

        Возвращает (free_left, free_right, left_zone, right_zone), где зоны —
        это (x1, x2) для отрисовки в debug-режиме.
        """
        margin = float(config.OBSTACLE_MARGIN)
        half_player = max(4.0, player.width / 2.0)
        edge = width * float(config.EDGE_MARGIN_RATIO)

        left_boundary = edge
        right_boundary = width - edge

        if threat is None:
            # Без угрозы «свободой» считаем расстояние до краёв/соседей
            for track in band:
                if track.right <= player.center_x:
                    left_boundary = max(left_boundary, track.right + margin)
                elif track.left >= player.center_x:
                    right_boundary = min(right_boundary, track.left - margin)
            free_left = max(0.0, player.center_x - half_player - left_boundary)
            free_right = max(0.0, right_boundary - player.center_x - half_player)
            return (
                free_left,
                free_right,
                (int(left_boundary), int(player.center_x)),
                (int(player.center_x), int(right_boundary)),
            )

        # Целевые точки для обхода угрозы слева / справа
        target_left = threat.left - margin - half_player
        target_right = threat.right + margin + half_player

        for track in band:
            if track.track_id == threat.track_id:
                continue
            if track.right <= threat.left:
                left_boundary = max(left_boundary, track.right + margin)
            elif track.left >= threat.right:
                right_boundary = min(right_boundary, track.left - margin)

        free_left = target_left - (left_boundary + half_player)
        free_right = (right_boundary - half_player) - target_right

        left_zone = (int(left_boundary), int(max(left_boundary, threat.left - margin)))
        right_zone = (int(min(right_boundary, threat.right + margin)), int(right_boundary))
        return free_left, free_right, left_zone, right_zone

    # ------------------------------------------------------------------
    def _choose_side(
        self,
        player: DetectedObject,
        threat: TrackedObstacle,
        free_left: float,
        free_right: float,
        left_ok: bool,
        right_ok: bool,
        width: int,
    ) -> str:
        """Выбрать сторону обхода: 'left', 'right' или '' (нет безопасной)."""
        if not left_ok and not right_ok:
            return ""
        if left_ok and not right_ok:
            return "left"
        if right_ok and not left_ok:
            return "right"

        # Обе стороны возможны — берём с бОльшим запасом.
        difference = free_left - free_right
        threshold = max(10.0, player.width * 0.35)
        if difference > threshold:
            return "left"
        if -difference > threshold:
            return "right"

        # Запас примерно одинаковый — идём в сторону, куда ближе двигаться.
        dist_left = abs(player.center_x - (threat.left - config.OBSTACLE_MARGIN))
        dist_right = abs((threat.right + config.OBSTACLE_MARGIN) - player.center_x)
        if dist_left < dist_right:
            return "left"
        if dist_right < dist_left:
            return "right"

        # Совсем симметрично — держимся ближе к центру кадра.
        return "left" if player.center_x > width * 0.5 else "right"

    # ------------------------------------------------------------------
    def apply(self, decision: Decision, keyboard, player: Optional[DetectedObject]) -> None:
        """
        Выполнить решение через KeyboardController.
        Здесь же — защита от спама клавишами.
        """
        action = decision.action

        if action == ACTION_JUMP:
            keyboard.stop_horizontal()
            keyboard.jump()
            return

        if action in (ACTION_LEFT, ACTION_CENTER_LEFT):
            if player is not None and decision.target_x is not None:
                if player.center_x <= decision.target_x:
                    keyboard.stop_horizontal()
                    return
            keyboard.press_left()
            return

        if action in (ACTION_RIGHT, ACTION_CENTER_RIGHT):
            if player is not None and decision.target_x is not None:
                if player.center_x >= decision.target_x:
                    keyboard.stop_horizontal()
                    return
            keyboard.press_right()
            return

        # ACTION_NONE / ACTION_HOLD
        keyboard.stop_horizontal()

"""
game_state.py — конечный автомат бота (state machine) и детектор GAME OVER.

Состояния:
    SEARCHING      — ищем игрока / препятствия
    MOVING_CENTER  — возвращаемся на центральную траекторию
    AVOID_LEFT     — уклоняемся влево
    AVOID_RIGHT    — уклоняемся вправо
    JUMPING        — выполняется прыжок
    RECOVERING     — препятствие пройдено, стабилизируемся
    DEAD           — GAME OVER
    PAUSED         — пауза (F6)
"""

from __future__ import annotations

import time
from enum import Enum
from typing import Dict, List, Optional, Tuple

import numpy as np

import config


class State(Enum):
    SEARCHING = "SEARCHING"
    MOVING_CENTER = "MOVING_CENTER"
    AVOID_LEFT = "AVOID_LEFT"
    AVOID_RIGHT = "AVOID_RIGHT"
    JUMPING = "JUMPING"
    RECOVERING = "RECOVERING"
    DEAD = "DEAD"
    PAUSED = "PAUSED"

    def __str__(self) -> str:  # pragma: no cover - косметика
        return self.value


# Допустимые переходы. PAUSED/DEAD доступны из любого состояния.
ALLOWED_TRANSITIONS: Dict[State, Tuple[State, ...]] = {
    State.SEARCHING: (State.MOVING_CENTER, State.AVOID_LEFT, State.AVOID_RIGHT, State.JUMPING),
    State.MOVING_CENTER: (State.SEARCHING, State.AVOID_LEFT, State.AVOID_RIGHT, State.JUMPING),
    State.AVOID_LEFT: (State.RECOVERING, State.AVOID_RIGHT, State.JUMPING, State.SEARCHING),
    State.AVOID_RIGHT: (State.RECOVERING, State.AVOID_LEFT, State.JUMPING, State.SEARCHING),
    State.JUMPING: (State.RECOVERING, State.SEARCHING, State.AVOID_LEFT, State.AVOID_RIGHT),
    State.RECOVERING: (State.MOVING_CENTER, State.SEARCHING, State.AVOID_LEFT, State.AVOID_RIGHT, State.JUMPING),
    State.DEAD: (State.SEARCHING,),
    State.PAUSED: (State.SEARCHING,),
}


class StateMachine:
    """Конечный автомат с историей переходов."""

    def __init__(self, initial: State = State.SEARCHING) -> None:
        self.state: State = initial
        self.previous_state: State = initial
        self.entered_at: float = time.time()
        self.history: List[Tuple[float, State]] = [(self.entered_at, initial)]

    # ------------------------------------------------------------------
    @property
    def time_in_state(self) -> float:
        return time.time() - self.entered_at

    # ------------------------------------------------------------------
    def can_transition(self, new_state: State) -> bool:
        if new_state == self.state:
            return True
        if new_state in (State.PAUSED, State.DEAD):
            return True
        if self.state in (State.PAUSED, State.DEAD):
            return new_state == State.SEARCHING
        return new_state in ALLOWED_TRANSITIONS.get(self.state, ())

    # ------------------------------------------------------------------
    def transition(self, new_state: State, force: bool = False) -> bool:
        """Перейти в новое состояние. Возвращает True, если переход выполнен."""
        if new_state == self.state:
            return False
        if not force and not self.can_transition(new_state):
            return False
        self.previous_state = self.state
        self.state = new_state
        self.entered_at = time.time()
        self.history.append((self.entered_at, new_state))
        if len(self.history) > 200:
            self.history = self.history[-200:]
        return True

    # ------------------------------------------------------------------
    def reset(self, state: State = State.SEARCHING) -> None:
        self.previous_state = self.state
        self.state = state
        self.entered_at = time.time()
        self.history.append((self.entered_at, state))

    def is_avoiding(self) -> bool:
        return self.state in (State.AVOID_LEFT, State.AVOID_RIGHT)


class GameOverDetector:
    """
    Обнаружение экрана GAME OVER.

    Признаки:
      * игрок не обнаруживается много кадров подряд;
      * картинка перестала меняться (статичный экран поверх игры);
      * экран стал почти полностью тёмным.
    """

    def __init__(self) -> None:
        self.lost_frames = 0
        self.static_frames = 0
        self._prev_small: Optional[np.ndarray] = None
        self.last_reason = ""
        self.mean_diff = 0.0

    # ------------------------------------------------------------------
    def reset(self) -> None:
        self.lost_frames = 0
        self.static_frames = 0
        self._prev_small = None
        self.last_reason = ""
        self.mean_diff = 0.0

    # ------------------------------------------------------------------
    def update(self, frame: Optional[np.ndarray], player_found: bool) -> bool:
        """Вернуть True, если похоже на GAME OVER."""
        if player_found:
            self.lost_frames = 0
        else:
            self.lost_frames += 1

        if frame is not None and frame.size > 0:
            small = self._downscale(frame)
            if self._prev_small is not None and small.shape == self._prev_small.shape:
                diff = np.abs(small.astype(np.int16) - self._prev_small.astype(np.int16))
                self.mean_diff = float(diff.mean())
                if self.mean_diff < float(config.GAME_OVER_DIFF_THRESHOLD):
                    self.static_frames += 1
                else:
                    self.static_frames = 0
            self._prev_small = small

            if float(frame.mean()) < float(config.GAME_OVER_DARK_V_MEAN):
                self.static_frames += 1

        if self.lost_frames >= int(config.GAME_OVER_LOST_FRAMES):
            self.last_reason = f"игрок не найден {self.lost_frames} кадров"
            return True
        if self.static_frames >= int(config.GAME_OVER_STATIC_FRAMES):
            self.last_reason = f"экран статичен {self.static_frames} кадров"
            return True
        return False

    # ------------------------------------------------------------------
    @staticmethod
    def _downscale(frame: np.ndarray) -> np.ndarray:
        """Уменьшить кадр без OpenCV (дешёвая операция каждые N кадров)."""
        step_y = max(1, frame.shape[0] // 48)
        step_x = max(1, frame.shape[1] // 64)
        return frame[::step_y, ::step_x]

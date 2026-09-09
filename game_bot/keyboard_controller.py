"""
keyboard_controller.py — безопасное управление клавиатурой.

Гарантии:
  * A и D никогда не зажаты одновременно;
  * SPACE не спамится (config.JUMP_COOLDOWN);
  * клавиша движения не удерживается дольше config.MOVE_MAX_HOLD;
  * release_all() отпускает всё — вызывается в finally / atexit / по ESC.
"""

from __future__ import annotations

import atexit
import time
from typing import Dict, Optional, Set

import config

try:  # pragma: no cover - зависит от окружения
    import pyautogui

    pyautogui.FAILSAFE = False
    pyautogui.PAUSE = 0.0
    _HAS_PYAUTOGUI = True
except Exception:  # pragma: no cover
    pyautogui = None
    _HAS_PYAUTOGUI = False


class KeyboardController:
    """Контроллер клавиатуры с защитой от залипания."""

    def __init__(self, dry_run: Optional[bool] = None) -> None:
        self.dry_run = bool(config.DRY_RUN if dry_run is None else dry_run)
        self.available = _HAS_PYAUTOGUI
        self.keys = config.movement_keys()

        self._held: Set[str] = set()
        self._hold_started: Dict[str, float] = {}
        self._last_jump: float = 0.0
        self._last_repress: Dict[str, float] = {}
        self.jump_count = 0
        self.press_count = 0
        self.last_action = "NONE"

        if not self.available and not self.dry_run:
            print(
                "[keyboard] PyAutoGUI не установлен — работаю в режиме DRY RUN.\n"
                "           Установка: pip install pyautogui"
            )
            self.dry_run = True

        atexit.register(self.release_all)

    # ------------------------------------------------------------------
    # Низкоуровневые операции
    # ------------------------------------------------------------------
    def refresh_keys(self) -> None:
        """Перечитать раскладку из config (например, после настроек)."""
        self.release_all()
        self.keys = config.movement_keys()

    def _key_down(self, key: str) -> None:
        if key in self._held:
            return
        if not self.dry_run and pyautogui is not None:
            try:
                pyautogui.keyDown(key)
            except Exception as exc:  # pragma: no cover
                print(f"[keyboard] keyDown({key}) error: {exc}")
                return
        self._held.add(key)
        self._hold_started[key] = time.time()
        self._last_repress[key] = time.time()
        self.press_count += 1

    def _key_up(self, key: str) -> None:
        if key not in self._held:
            return
        if not self.dry_run and pyautogui is not None:
            try:
                pyautogui.keyUp(key)
            except Exception as exc:  # pragma: no cover
                print(f"[keyboard] keyUp({key}) error: {exc}")
        self._held.discard(key)
        self._hold_started.pop(key, None)
        self._last_repress.pop(key, None)

    def _tap(self, key: str) -> None:
        if not self.dry_run and pyautogui is not None:
            try:
                pyautogui.keyDown(key)
                pyautogui.keyUp(key)
            except Exception as exc:  # pragma: no cover
                print(f"[keyboard] tap({key}) error: {exc}")
        self.press_count += 1

    # ------------------------------------------------------------------
    # Публичный интерфейс (как в ТЗ)
    # ------------------------------------------------------------------
    def press_left(self) -> None:
        """Зажать «влево» (A). Одновременно с «вправо» — невозможно."""
        self.release_right()
        self._key_down(self.keys["left"])
        self.last_action = "MOVE LEFT"

    def press_right(self) -> None:
        """Зажать «вправо» (D)."""
        self.release_left()
        self._key_down(self.keys["right"])
        self.last_action = "MOVE RIGHT"

    def release_left(self) -> None:
        self._key_up(self.keys["left"])

    def release_right(self) -> None:
        self._key_up(self.keys["right"])

    def press_up(self) -> None:
        self.release_down()
        self._key_down(self.keys["up"])
        self.last_action = "MOVE UP"

    def press_down(self) -> None:
        self.release_up()
        self._key_down(self.keys["down"])
        self.last_action = "MOVE DOWN"

    def release_up(self) -> None:
        self._key_up(self.keys["up"])

    def release_down(self) -> None:
        self._key_up(self.keys["down"])

    def release_space(self) -> None:
        self._key_up(self.keys["jump"])

    # ------------------------------------------------------------------
    def can_jump(self, now: Optional[float] = None) -> bool:
        now = time.time() if now is None else now
        return (now - self._last_jump) >= float(config.JUMP_COOLDOWN)

    def jump(self) -> bool:
        """
        Прыжок (SPACE) с учётом кулдауна.
        Возвращает True, если прыжок реально выполнен.
        """
        now = time.time()
        if not self.can_jump(now):
            return False
        self._last_jump = now
        self.jump_count += 1
        self._tap(self.keys["jump"])
        self.last_action = "JUMP"
        return True

    def tap_key(self, key: str) -> None:
        """Разовое нажатие произвольной клавиши (например, R для рестарта)."""
        self._tap(key)

    # ------------------------------------------------------------------
    def stop_horizontal(self) -> None:
        """Отпустить обе горизонтальные клавиши."""
        self.release_left()
        self.release_right()
        if self.last_action in ("MOVE LEFT", "MOVE RIGHT"):
            self.last_action = "IDLE"

    def release_all(self) -> None:
        """
        КРИТИЧНО: отпустить абсолютно все зажатые клавиши.
        Безопасно вызывать многократно, в т.ч. из atexit и finally.
        """
        for key in list(self._held):
            self._key_up(key)
        # Дополнительная страховка: явно отпускаем все известные клавиши.
        if not self.dry_run and pyautogui is not None:
            for key in set(self.keys.values()) | {
                config.KEY_LEFT,
                config.KEY_RIGHT,
                config.KEY_UP,
                config.KEY_DOWN,
                config.ARROW_LEFT,
                config.ARROW_RIGHT,
                config.ARROW_UP,
                config.ARROW_DOWN,
                config.KEY_JUMP,
            }:
                try:
                    pyautogui.keyUp(key)
                except Exception:  # pragma: no cover
                    pass
        self._held.clear()
        self._hold_started.clear()
        self._last_repress.clear()
        self.last_action = "RELEASED"

    # ------------------------------------------------------------------
    def update(self, now: Optional[float] = None) -> None:
        """
        Вызывать каждый кадр: страхует от «залипания» клавиш,
        если бот по какой-то причине забыл их отпустить.
        """
        now = time.time() if now is None else now
        max_hold = float(config.MOVE_MAX_HOLD)
        for key in list(self._held):
            started = self._hold_started.get(key, now)
            if key == self.keys["jump"]:
                self._key_up(key)
                continue
            if now - started > max_hold:
                self._key_up(key)
                continue
            interval = float(config.KEY_REPRESS_INTERVAL)
            if interval > 0 and now - self._last_repress.get(key, now) >= interval:
                # Некоторым играм нужно «передёргивание» клавиши
                self._key_up(key)
                self._key_down(key)

    # ------------------------------------------------------------------
    @property
    def held_keys(self) -> Set[str]:
        return set(self._held)

    def is_holding(self, direction: str) -> bool:
        key = self.keys.get(direction)
        return key is not None and key in self._held

    def status(self) -> str:
        held = ",".join(sorted(self._held)) or "-"
        mode = "DRY" if self.dry_run else "LIVE"
        return f"[{mode}] held={held} jumps={self.jump_count}"

    # ------------------------------------------------------------------
    def __enter__(self) -> "KeyboardController":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.release_all()

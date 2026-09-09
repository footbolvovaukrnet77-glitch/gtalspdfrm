"""
hotkeys.py — глобальные горячие клавиши.

F6  — старт / пауза
F7  — полная остановка
F8  — вкл/выкл DEBUG
ESC — аварийная остановка (все клавиши отпускаются)

Бэкенды: pynput (предпочтительно) или keyboard. Если ни один недоступен,
управление остаётся через окно OpenCV (клавиши в debug-окне).
"""

from __future__ import annotations

import threading
from typing import Callable, Dict, Optional

import config

try:  # pragma: no cover
    from pynput import keyboard as pynput_keyboard

    _HAS_PYNPUT = True
except Exception:  # pragma: no cover
    pynput_keyboard = None
    _HAS_PYNPUT = False

try:  # pragma: no cover
    import keyboard as keyboard_lib

    _HAS_KEYBOARD = True
except Exception:  # pragma: no cover
    keyboard_lib = None
    _HAS_KEYBOARD = False


class HotkeyManager:
    """Слушатель горячих клавиш в отдельном потоке."""

    def __init__(self, callbacks: Dict[str, Callable[[], None]]) -> None:
        """
        callbacks: {"start_pause": fn, "stop": fn, "debug": fn, "emergency": fn}
        """
        self.callbacks = callbacks
        self.backend = "none"
        self._listener = None
        self._lock = threading.Lock()

    # ------------------------------------------------------------------
    def start(self) -> str:
        if _HAS_PYNPUT:
            try:
                self._start_pynput()
                self.backend = "pynput"
                return self.backend
            except Exception as exc:  # pragma: no cover
                print(f"[hotkeys] pynput недоступен: {exc}")
        if _HAS_KEYBOARD:
            try:
                self._start_keyboard_lib()
                self.backend = "keyboard"
                return self.backend
            except Exception as exc:  # pragma: no cover
                print(f"[hotkeys] keyboard недоступен: {exc}")
        self.backend = "none"
        print(
            "[hotkeys] Глобальные горячие клавиши недоступны.\n"
            "          Установите: pip install pynput\n"
            "          Пока управляйте ботом из debug-окна (F6/F7/F8/ESC)."
        )
        return self.backend

    # ------------------------------------------------------------------
    def _fire(self, name: str) -> None:
        callback = self.callbacks.get(name)
        if callback is None:
            return
        with self._lock:
            try:
                callback()
            except Exception as exc:  # pragma: no cover
                print(f"[hotkeys] ошибка обработчика {name}: {exc}")

    # ------------------------------------------------------------------
    def _start_pynput(self) -> None:  # pragma: no cover - требует дисплея
        key_map = {
            config.HOTKEY_START_PAUSE.lower(): "start_pause",
            config.HOTKEY_STOP.lower(): "stop",
            config.HOTKEY_DEBUG.lower(): "debug",
            config.HOTKEY_EMERGENCY.lower(): "emergency",
        }

        def on_press(key) -> None:
            name = None
            if isinstance(key, pynput_keyboard.Key):
                name = key.name
            elif getattr(key, "char", None):
                name = key.char
            if name is None:
                return
            action = key_map.get(str(name).lower())
            if action:
                self._fire(action)

        self._listener = pynput_keyboard.Listener(on_press=on_press)
        self._listener.daemon = True
        self._listener.start()

    # ------------------------------------------------------------------
    def _start_keyboard_lib(self) -> None:  # pragma: no cover
        keyboard_lib.add_hotkey(config.HOTKEY_START_PAUSE, lambda: self._fire("start_pause"))
        keyboard_lib.add_hotkey(config.HOTKEY_STOP, lambda: self._fire("stop"))
        keyboard_lib.add_hotkey(config.HOTKEY_DEBUG, lambda: self._fire("debug"))
        keyboard_lib.add_hotkey(config.HOTKEY_EMERGENCY, lambda: self._fire("emergency"))

    # ------------------------------------------------------------------
    def handle_window_key(self, key_code: int) -> None:
        """
        Обработать клавишу из окна OpenCV (waitKey).
        Работает даже без pynput, если debug-окно в фокусе.
        """
        if key_code in (255, -1):
            return
        # OpenCV: ESC = 27, F6..F8 могут не приходить — дублируем буквами.
        if key_code == 27:
            self._fire("emergency")
        elif key_code in (ord("p"), ord(" ")):
            self._fire("start_pause")
        elif key_code == ord("x"):
            self._fire("stop")
        elif key_code == ord("d"):
            self._fire("debug")

    # ------------------------------------------------------------------
    def stop(self) -> None:
        if self.backend == "pynput" and self._listener is not None:  # pragma: no cover
            try:
                self._listener.stop()
            except Exception:
                pass
            self._listener = None
        elif self.backend == "keyboard" and _HAS_KEYBOARD:  # pragma: no cover
            try:
                keyboard_lib.clear_all_hotkeys()
            except Exception:
                pass

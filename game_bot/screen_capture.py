"""
screen_capture.py — быстрый захват области экрана.

Основной бэкенд — MSS. Если MSS недоступен, используется PyAutoGUI
(медленнее, но работает). Захватывается ТОЛЬКО область игры.
"""

from __future__ import annotations

import time
from typing import Dict, Optional

import numpy as np

import config

try:  # pragma: no cover - зависит от окружения
    import mss

    _HAS_MSS = True
except Exception:  # pragma: no cover
    mss = None
    _HAS_MSS = False

try:  # pragma: no cover
    import cv2

    _HAS_CV2 = True
except Exception:  # pragma: no cover
    cv2 = None
    _HAS_CV2 = False


class ScreenCapture:
    """
    Захват прямоугольной области экрана.

    Использование:
        with ScreenCapture(config.GAME_REGION) as cap:
            frame = cap.grab()      # BGR numpy-массив
    """

    def __init__(self, region: Optional[Dict[str, int]] = None):
        self.region = dict(region or config.GAME_REGION)
        self._validate_region()
        self._sct = None
        self._backend = "none"
        self._last_frame: Optional[np.ndarray] = None
        self._frame_count = 0
        self._start_time = time.time()
        self.open()

    # ------------------------------------------------------------------
    def _validate_region(self) -> None:
        for key in ("left", "top", "width", "height"):
            if key not in self.region:
                raise ValueError(f"GAME_REGION: отсутствует ключ '{key}'")
            self.region[key] = int(self.region[key])
        if self.region["width"] <= 0 or self.region["height"] <= 0:
            raise ValueError("GAME_REGION: width и height должны быть > 0")

    # ------------------------------------------------------------------
    def open(self) -> None:
        if _HAS_MSS:
            try:
                self._sct = mss.mss()
                self._backend = "mss"
                return
            except Exception as exc:  # pragma: no cover
                print(f"[capture] MSS недоступен ({exc}), пробую PyAutoGUI...")
        try:  # pragma: no cover
            import pyautogui  # noqa: F401

            self._backend = "pyautogui"
        except Exception as exc:  # pragma: no cover
            self._backend = "none"
            print(f"[capture] Нет доступного бэкенда захвата экрана: {exc}")

    # ------------------------------------------------------------------
    @property
    def backend(self) -> str:
        return self._backend

    def set_region(self, region: Dict[str, int]) -> None:
        self.region = dict(region)
        self._validate_region()

    # ------------------------------------------------------------------
    def grab(self) -> Optional[np.ndarray]:
        """Вернуть кадр области игры в формате BGR (numpy uint8) или None."""
        frame = None
        if self._backend == "mss" and self._sct is not None:
            monitor = {
                "left": self.region["left"],
                "top": self.region["top"],
                "width": self.region["width"],
                "height": self.region["height"],
            }
            raw = self._sct.grab(monitor)
            frame = np.asarray(raw, dtype=np.uint8)  # BGRA
            frame = frame[:, :, :3]                  # -> BGR
        elif self._backend == "pyautogui":  # pragma: no cover
            import pyautogui

            shot = pyautogui.screenshot(
                region=(
                    self.region["left"],
                    self.region["top"],
                    self.region["width"],
                    self.region["height"],
                )
            )
            frame = np.array(shot, dtype=np.uint8)   # RGB
            if _HAS_CV2:
                frame = cv2.cvtColor(frame, cv2.COLOR_RGB2BGR)
            else:
                frame = frame[:, :, ::-1].copy()
        else:
            return None

        frame = np.ascontiguousarray(frame)
        self._last_frame = frame
        self._frame_count += 1
        return frame

    # ------------------------------------------------------------------
    def preprocess(self, frame: np.ndarray) -> np.ndarray:
        """
        Лёгкая предобработка: масштабирование и подавление шума.
        Тяжёлых операций здесь нет — вызывается каждый кадр.
        """
        if frame is None:
            return frame
        out = frame
        scale = float(config.PROCESS_SCALE)
        if _HAS_CV2 and 0.1 <= scale < 0.999:
            new_w = max(1, int(out.shape[1] * scale))
            new_h = max(1, int(out.shape[0] * scale))
            out = cv2.resize(out, (new_w, new_h), interpolation=cv2.INTER_AREA)
        k = int(config.BLUR_KERNEL)
        if _HAS_CV2 and k >= 3:
            if k % 2 == 0:
                k += 1
            out = cv2.GaussianBlur(out, (k, k), 0)
        return out

    # ------------------------------------------------------------------
    @property
    def last_frame(self) -> Optional[np.ndarray]:
        return self._last_frame

    def average_fps(self) -> float:
        elapsed = time.time() - self._start_time
        if elapsed <= 0:
            return 0.0
        return self._frame_count / elapsed

    # ------------------------------------------------------------------
    def close(self) -> None:
        if self._sct is not None:
            try:
                self._sct.close()
            except Exception:
                pass
            self._sct = None

    def __enter__(self) -> "ScreenCapture":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()


def grab_fullscreen() -> Optional[np.ndarray]:
    """Скриншот всего экрана (нужен для калибровки области)."""
    if _HAS_MSS:
        try:
            with mss.mss() as sct:
                monitor = sct.monitors[0]  # объединённый виртуальный экран
                raw = sct.grab(monitor)
                frame = np.asarray(raw, dtype=np.uint8)[:, :, :3]
                return np.ascontiguousarray(frame)
        except Exception as exc:  # pragma: no cover
            print(f"[capture] MSS fullscreen error: {exc}")
    try:  # pragma: no cover
        import pyautogui

        shot = pyautogui.screenshot()
        frame = np.array(shot, dtype=np.uint8)
        if _HAS_CV2:
            return cv2.cvtColor(frame, cv2.COLOR_RGB2BGR)
        return frame[:, :, ::-1].copy()
    except Exception as exc:  # pragma: no cover
        print(f"[capture] Не удалось сделать скриншот экрана: {exc}")
        return None


def screen_offset() -> Dict[str, int]:
    """
    Смещение объединённого виртуального экрана (для мультимонитора).
    Нужно, чтобы координаты выделения на скриншоте соответствовали экрану.
    """
    if _HAS_MSS:
        try:
            with mss.mss() as sct:
                monitor = sct.monitors[0]
                return {"left": int(monitor["left"]), "top": int(monitor["top"])}
        except Exception:  # pragma: no cover
            pass
    return {"left": 0, "top": 0}

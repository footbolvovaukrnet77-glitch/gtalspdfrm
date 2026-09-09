"""
calibration.py — калибровка бота.

Возможности:
  1. Выбор области игры мышью (рамка на скриншоте экрана).
  2. Подбор HSV игрока и препятствий кликами по живому изображению.
  3. Просмотр масок в реальном времени.
  4. Сохранение всех настроек в config.json.

Запуск отдельно:  python calibration.py
Или из меню:      python main.py -> [2] Калибровка
"""

from __future__ import annotations

import time
from typing import Dict, List, Optional, Tuple

import cv2
import numpy as np

import config
import screen_capture
from vision_utils import clean_mask, color_mask, to_hsv

WINDOW_REGION = "Calibration: select game region"
WINDOW_COLOR = "Calibration: colors (click on object)"
WINDOW_MASK = "Calibration: mask"

MAX_PREVIEW_W = 1280
MAX_PREVIEW_H = 720


# ----------------------------------------------------------------------
# 1. ВЫБОР ОБЛАСТИ ИГРЫ
# ----------------------------------------------------------------------
def select_region(interactive: bool = True) -> Optional[Dict[str, int]]:
    """
    Показать скриншот экрана и позволить выделить область игры мышью.
    Возвращает GAME_REGION или None, если пользователь отменил.
    """
    print("\n=== ВЫБОР ОБЛАСТИ ИГРЫ ===")
    print("Переключитесь на окно игры — скриншот будет сделан через 4 секунды.")
    if interactive:
        for remaining in range(4, 0, -1):
            print(f"  {remaining}...", end="\r", flush=True)
            time.sleep(1)
        print("  Снимок сделан.      ")

    shot = screen_capture.grab_fullscreen()
    if shot is None:
        print("Не удалось получить скриншот экрана.")
        return None

    offset = screen_capture.screen_offset()
    height, width = shot.shape[:2]
    scale = min(1.0, MAX_PREVIEW_W / float(width), MAX_PREVIEW_H / float(height))
    preview = (
        cv2.resize(shot, (int(width * scale), int(height * scale)), interpolation=cv2.INTER_AREA)
        if scale < 1.0
        else shot.copy()
    )

    print("Выделите область игры мышью, затем нажмите ENTER (или SPACE).")
    print("Отмена — клавиша C.")
    cv2.namedWindow(WINDOW_REGION, cv2.WINDOW_NORMAL)
    roi = cv2.selectROI(WINDOW_REGION, preview, showCrosshair=True, fromCenter=False)
    cv2.destroyWindow(WINDOW_REGION)
    cv2.waitKey(1)

    x, y, w, h = [int(v) for v in roi]
    if w <= 0 or h <= 0:
        print("Область не выбрана.")
        return None

    inv = 1.0 / scale if scale > 0 else 1.0
    region = {
        "left": int(round(x * inv)) + int(offset["left"]),
        "top": int(round(y * inv)) + int(offset["top"]),
        "width": int(round(w * inv)),
        "height": int(round(h * inv)),
    }
    print(f"Выбрана область: {region}")
    return region


# ----------------------------------------------------------------------
# 2. КАЛИБРОВКА ЦВЕТОВ
# ----------------------------------------------------------------------
class ColorPicker:
    """Накопление HSV-образцов и построение диапазона."""

    def __init__(self, name: str) -> None:
        self.name = name
        self.samples: List[Tuple[int, int, int]] = []
        self.h_tol = 10
        self.s_tol = 70
        self.v_tol = 70

    def add(self, hsv_pixel: Tuple[int, int, int]) -> None:
        self.samples.append(tuple(int(v) for v in hsv_pixel))  # type: ignore[arg-type]

    def clear(self) -> None:
        self.samples.clear()

    def range(self) -> Optional[Tuple[List[int], List[int]]]:
        if not self.samples:
            return None
        arr = np.array(self.samples, dtype=np.int32)
        h_min, s_min, v_min = arr.min(axis=0)
        h_max, s_max, v_max = arr.max(axis=0)
        lower = [
            int(max(0, h_min - self.h_tol)),
            int(max(0, s_min - self.s_tol)),
            int(max(0, v_min - self.v_tol)),
        ]
        upper = [
            int(min(179, h_max + self.h_tol)),
            int(min(255, s_max + self.s_tol)),
            int(min(255, v_max + self.v_tol)),
        ]
        return lower, upper

    def describe(self) -> str:
        rng = self.range()
        if rng is None:
            return f"{self.name}: нет образцов"
        lower, upper = rng
        return f"{self.name}: lower={lower} upper={upper} (образцов: {len(self.samples)})"


def calibrate_colors(region: Optional[Dict[str, int]] = None) -> bool:
    """
    Живая калибровка цветов.

    Управление:
      ЛКМ    — взять образец цвета в текущем режиме
      P      — режим ИГРОК
      O      — режим ПРЕПЯТСТВИЕ
      T      — режим ДОРОЖКА (зелёные линии)
      C      — очистить образцы текущего режима
      +/-    — увеличить/уменьшить допуск по оттенку
      M      — показать/скрыть маску
      S      — сохранить в config.json
      Q/ESC  — выход
    """
    region = region or config.GAME_REGION
    picker_map = {
        "player": ColorPicker("PLAYER"),
        "obstacle": ColorPicker("OBSTACLE"),
        "track": ColorPicker("TRACK"),
    }
    mode = "player"
    show_mask = True
    saved = False

    state = {"click": None}

    def on_mouse(event: int, x: int, y: int, flags: int, param) -> None:
        if event == cv2.EVENT_LBUTTONDOWN:
            state["click"] = (x, y)

    print("\n=== КАЛИБРОВКА ЦВЕТОВ ===")
    print(calibrate_colors.__doc__)

    try:
        capture = screen_capture.ScreenCapture(region)
    except Exception as exc:
        print(f"Не удалось начать захват: {exc}")
        return False

    if capture.backend == "none":
        print(
            "Нет доступного бэкенда захвата экрана.\n"
            "Установите: pip install mss   (или pip install pyautogui pillow)"
        )
        capture.close()
        return False

    cv2.namedWindow(WINDOW_COLOR, cv2.WINDOW_NORMAL)
    cv2.setMouseCallback(WINDOW_COLOR, on_mouse)

    try:
        while True:
            frame = capture.grab()
            if frame is None:
                print("Кадр не получен — прерываю.")
                break
            hsv = to_hsv(frame)

            if state["click"] is not None:
                cx, cy = state["click"]
                state["click"] = None
                if 0 <= cy < hsv.shape[0] and 0 <= cx < hsv.shape[1]:
                    patch = hsv[
                        max(0, cy - 2) : cy + 3,
                        max(0, cx - 2) : cx + 3,
                    ].reshape(-1, 3)
                    median = np.median(patch, axis=0)
                    picker_map[mode].add(tuple(int(v) for v in median))
                    print(
                        f"[{mode}] пиксель ({cx},{cy}) HSV={tuple(int(v) for v in median)}  "
                        f"-> {picker_map[mode].describe()}"
                    )

            display = frame.copy()
            lines = [
                f"MODE: {mode.upper()}  (P/O/T)",
                picker_map["player"].describe(),
                picker_map["obstacle"].describe(),
                picker_map["track"].describe(),
                f"H tol: {picker_map[mode].h_tol}  (+/-)   mask: {'on' if show_mask else 'off'} (M)",
                "S - save to config.json,  C - clear,  Q - quit",
            ]
            y = 20
            for line in lines:
                cv2.putText(
                    display, line, (10, y), cv2.FONT_HERSHEY_SIMPLEX, 0.45,
                    (0, 0, 0), 3, cv2.LINE_AA,
                )
                cv2.putText(
                    display, line, (10, y), cv2.FONT_HERSHEY_SIMPLEX, 0.45,
                    (255, 255, 255), 1, cv2.LINE_AA,
                )
                y += 18

            cv2.imshow(WINDOW_COLOR, display)

            if show_mask:
                rng = picker_map[mode].range()
                if rng is not None:
                    mask = color_mask(hsv, rng[0], rng[1])
                    mask = clean_mask(mask, config.MORPH_KERNEL)
                else:
                    mask = np.zeros(frame.shape[:2], dtype=np.uint8)
                cv2.imshow(WINDOW_MASK, mask)

            key = cv2.waitKey(30) & 0xFF
            if key in (ord("q"), 27):
                break
            if key == ord("p"):
                mode = "player"
            elif key == ord("o"):
                mode = "obstacle"
            elif key == ord("t"):
                mode = "track"
            elif key == ord("c"):
                picker_map[mode].clear()
                print(f"[{mode}] образцы очищены")
            elif key in (ord("+"), ord("=")):
                picker_map[mode].h_tol = min(60, picker_map[mode].h_tol + 2)
            elif key in (ord("-"), ord("_")):
                picker_map[mode].h_tol = max(0, picker_map[mode].h_tol - 2)
            elif key == ord("m"):
                show_mask = not show_mask
                if not show_mask:
                    cv2.destroyWindow(WINDOW_MASK)
            elif key == ord("s"):
                saved = _apply_and_save(picker_map, region)
    finally:
        capture.close()
        cv2.destroyAllWindows()
        cv2.waitKey(1)

    return saved


def _apply_and_save(picker_map: Dict[str, ColorPicker], region: Dict[str, int]) -> bool:
    """Записать подобранные диапазоны в config и сохранить config.json."""
    player_range = picker_map["player"].range()
    obstacle_range = picker_map["obstacle"].range()
    track_range = picker_map["track"].range()

    if player_range:
        config.PLAYER_HSV_LOWER, config.PLAYER_HSV_UPPER = player_range
    if obstacle_range:
        config.OBSTACLE_HSV_LOWER, config.OBSTACLE_HSV_UPPER = obstacle_range
    if track_range:
        config.TRACK_HSV_LOWER, config.TRACK_HSV_UPPER = track_range
    config.GAME_REGION = dict(region)

    ok = config.save()
    if ok:
        print("Настройки цветов сохранены.")
    return ok


# ----------------------------------------------------------------------
# 3. ПРОВЕРКА ДЕТЕКЦИИ
# ----------------------------------------------------------------------
def preview_detection(region: Optional[Dict[str, int]] = None, seconds: float = 0.0) -> None:
    """
    Живой предпросмотр: как бот видит игрока и препятствия.
    Полезно, чтобы убедиться в правильности HSV-настроек. Выход — Q/ESC.
    """
    from obstacle_detector import ObstacleDetector
    from player_detector import PlayerDetector

    region = region or config.GAME_REGION
    capture = screen_capture.ScreenCapture(region)
    if capture.backend == "none":
        print("Нет бэкенда захвата экрана (установите mss).")
        capture.close()
        return

    player_detector = PlayerDetector()
    obstacle_detector = ObstacleDetector()
    started = time.time()
    print("Предпросмотр детекции. Выход — Q или ESC.")

    try:
        while True:
            frame = capture.grab()
            if frame is None:
                break
            frame = capture.preprocess(frame)
            player = player_detector.detect(frame)
            obstacles = obstacle_detector.detect(frame, player)

            display = frame.copy()
            for obstacle in obstacles:
                cv2.rectangle(
                    display,
                    (obstacle.left, obstacle.top),
                    (obstacle.right, obstacle.bottom),
                    (0, 0, 255),
                    2,
                )
            if player is not None:
                cv2.rectangle(
                    display,
                    (player.left, player.top),
                    (player.right, player.bottom),
                    (0, 255, 0),
                    2,
                )
            cv2.putText(
                display,
                f"player: {'OK' if player else 'NOT FOUND'}   obstacles: {len(obstacles)}",
                (10, 22),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.5,
                (255, 255, 255),
                1,
                cv2.LINE_AA,
            )
            cv2.imshow("Detection preview", display)
            key = cv2.waitKey(20) & 0xFF
            if key in (ord("q"), 27):
                break
            if seconds and (time.time() - started) > seconds:
                break
    finally:
        capture.close()
        cv2.destroyAllWindows()
        cv2.waitKey(1)


# ----------------------------------------------------------------------
def run() -> None:
    """Интерактивное меню калибровки."""
    config.load(verbose=False)
    while True:
        print(
            "\n=== КАЛИБРОВКА ===\n"
            f"Текущая область игры: {config.GAME_REGION}\n"
            f"PLAYER   HSV: {config.PLAYER_HSV_LOWER} .. {config.PLAYER_HSV_UPPER}\n"
            f"OBSTACLE HSV: {config.OBSTACLE_HSV_LOWER} .. {config.OBSTACLE_HSV_UPPER}\n"
            f"TRACK    HSV: {config.TRACK_HSV_LOWER} .. {config.TRACK_HSV_UPPER}\n"
            "\n"
            "  [1] Выбрать область игры мышью\n"
            "  [2] Калибровать цвета (игрок / препятствия / дорожка)\n"
            "  [3] Предпросмотр детекции\n"
            "  [4] Сохранить настройки в config.json\n"
            "  [5] Сбросить настройки к значениям из config.py\n"
            "  [0] Назад\n"
        )
        choice = input("Выбор: ").strip()
        if choice == "1":
            region = select_region()
            if region:
                config.GAME_REGION = region
                config.save()
        elif choice == "2":
            calibrate_colors(config.GAME_REGION)
        elif choice == "3":
            preview_detection(config.GAME_REGION)
        elif choice == "4":
            config.save()
        elif choice == "5":
            config.reset_to_defaults()
            print("Настройки сброшены (не забудьте сохранить, пункт [4]).")
        elif choice in ("0", "q", "exit"):
            return
        else:
            print("Неизвестный пункт меню.")


if __name__ == "__main__":
    run()

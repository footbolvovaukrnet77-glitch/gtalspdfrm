"""
config.py — единый файл настроек бота.

Все параметры можно менять здесь, либо через config.json
(config.json перекрывает значения из этого файла при загрузке).

Формат HSV в OpenCV:
    H: 0..179
    S: 0..255
    V: 0..255
"""

from __future__ import annotations

import copy
import json
import os
from typing import Any, Dict, List

# --------------------------------------------------------------------------
# Пути
# --------------------------------------------------------------------------
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_JSON_PATH = os.path.join(BASE_DIR, "config.json")

# --------------------------------------------------------------------------
# 1. ОБЛАСТЬ ЗАХВАТА ЭКРАНА
# --------------------------------------------------------------------------
# Если координаты неизвестны — запустите калибровку (main.py -> [2]).
GAME_REGION: Dict[str, int] = {
    "left": 100,
    "top": 100,
    "width": 800,
    "height": 600,
}

# --------------------------------------------------------------------------
# 2. ПРОИЗВОДИТЕЛЬНОСТЬ
# --------------------------------------------------------------------------
TARGET_FPS = 30            # целевой FPS основного цикла
DEBUG_FPS = 15             # FPS при включённом DEBUG_MODE (можно снизить)
PROCESS_SCALE = 1.0        # <1.0 — уменьшать кадр перед обработкой (быстрее).
                           # ВНИМАНИЕ: все дистанции ниже задаются в пикселях
                           # обработанного кадра, поэтому при изменении этого
                           # параметра пропорционально меняйте SAFE_DISTANCE,
                           # REACTION_DISTANCE, JUMP_DISTANCE, PLAYER_WIDTH.
BLUR_KERNEL = 3            # лёгкое размытие для подавления шума (0 = выкл)

# --------------------------------------------------------------------------
# 3. DEBUG
# --------------------------------------------------------------------------
DEBUG_MODE = True          # окно OpenCV с отладочной отрисовкой
DEBUG_WINDOW_NAME = "GameBot Debug"
DEBUG_SHOW_MASKS = False   # дополнительно показывать маски игрока/препятствий

# --------------------------------------------------------------------------
# 4. ЦВЕТА (HSV)
# --------------------------------------------------------------------------
# Игрок: маленький робот внизу экрана.
# По умолчанию — светло-серый/белый корпус с небольшим насыщением.
PLAYER_HSV_LOWER: List[int] = [0, 0, 170]
PLAYER_HSV_UPPER: List[int] = [179, 60, 255]

# Дополнительные диапазоны игрока (например, цветная подсветка робота).
# Каждый элемент: {"lower": [h,s,v], "upper": [h,s,v]}
PLAYER_HSV_EXTRA_RANGES: List[Dict[str, List[int]]] = []

# Препятствия: яркая розовая планка + голубые стойки.
OBSTACLE_HSV_LOWER: List[int] = [140, 90, 120]     # розовый / magenta
OBSTACLE_HSV_UPPER: List[int] = [175, 255, 255]

# Дополнительные диапазоны препятствий (голубые стойки и т.п.).
OBSTACLE_HSV_EXTRA_RANGES: List[Dict[str, List[int]]] = [
    {"lower": [85, 90, 120], "upper": [105, 255, 255]},   # голубой / cyan
]

# Цвет дорожки (зелёные линии). Используется, чтобы НЕ считать дорожку
# препятствием и (опционально) чтобы оценивать границы трассы.
TRACK_HSV_LOWER: List[int] = [40, 60, 60]
TRACK_HSV_UPPER: List[int] = [85, 255, 255]
USE_TRACK_MASK = True      # вычитать дорожку из маски препятствий

# --------------------------------------------------------------------------
# 5. ФИЛЬТРАЦИЯ ОБЪЕКТОВ
# --------------------------------------------------------------------------
# Значения в долях от размера кадра — не зависят от разрешения.
PLAYER_MIN_AREA_RATIO = 0.00015    # мин. площадь игрока (доля площади кадра)
PLAYER_MAX_AREA_RATIO = 0.08
PLAYER_SEARCH_TOP_RATIO = 0.55     # игрока ищем ниже этой доли высоты кадра
PLAYER_MAX_ASPECT = 4.0            # w/h и h/w не больше этого

OBSTACLE_MIN_AREA_RATIO = 0.00030
OBSTACLE_MAX_AREA_RATIO = 0.35
OBSTACLE_MIN_WIDTH_RATIO = 0.02    # мин. ширина препятствия (доля ширины кадра)
OBSTACLE_MIN_HEIGHT_RATIO = 0.008
OBSTACLE_SEARCH_BOTTOM_RATIO = 0.98  # ниже этого препятствия игнорируем
OBSTACLE_MERGE_DISTANCE_RATIO = 0.03 # склеивание близких кусков одного объекта
MORPH_KERNEL = 5                     # ядро морфологии для очистки масок

# Сколько кадров держать последнее известное положение игрока
PLAYER_MEMORY_FRAMES = 45

# --------------------------------------------------------------------------
# 6. ГЕОМЕТРИЯ / ЛОГИКА УКЛОНЕНИЯ
# --------------------------------------------------------------------------
# Все "расстояния" — в пикселях кадра (кадр = область игры).
PLAYER_WIDTH = 60          # ожидаемая ширина игрока (если детектор не нашёл)
OBSTACLE_MARGIN = 18       # запас по горизонтали при обходе
SAFE_DISTANCE = 90         # ближе этого — критическая зона
REACTION_DISTANCE = 300    # с этого расстояния бот начинает реагировать
JUMP_DISTANCE = 190        # макс. расстояние, на котором допустим прыжок (px)
JUMP_TIME_AHEAD = 0.35     # за сколько секунд до столкновения прыгать (если
                           # скорость препятствия известна)
LANE_TOLERANCE = 25        # допустимое отклонение от центральной траектории
CENTER_X_RATIO = 0.5       # где проходит "центральная траектория" (0..1)
SIDE_SCAN_WIDTH_RATIO = 0.45  # ширина зоны слева/справа для оценки свободы
EDGE_MARGIN_RATIO = 0.06   # мёртвая зона у краёв кадра

# Препятствие считается "низким" (перепрыгиваемым), если его высота меньше:
JUMPABLE_HEIGHT_RATIO = 0.16   # доля высоты кадра
# Препятствие считается "широким" (обойти нельзя), если ширина больше:
UNAVOIDABLE_WIDTH_RATIO = 0.55 # доля ширины кадра

# --------------------------------------------------------------------------
# 7. ТРЕКИНГ / ПРОГНОЗ
# --------------------------------------------------------------------------
TRACK_MATCH_DISTANCE_RATIO = 0.18  # радиус сопоставления объектов между кадрами
TRACK_MAX_MISSES = 6               # сколько кадров держать потерянный трек
TRACK_SPEED_SMOOTHING = 0.6        # сглаживание скорости (0..1, больше = плавнее)
PREDICTION_TIME = 0.35             # на сколько секунд вперёд прогнозируем (сек)
MIN_SPEED_FOR_PREDICTION = 5.0     # px/сек — ниже считаем объект статичным

# --------------------------------------------------------------------------
# 8. КЛАВИАТУРА
# --------------------------------------------------------------------------
KEY_LEFT = "a"
KEY_RIGHT = "d"
KEY_UP = "w"
KEY_DOWN = "s"
KEY_JUMP = "space"

USE_ARROW_KEYS = False     # True — использовать стрелки вместо WASD
ARROW_LEFT = "left"
ARROW_RIGHT = "right"
ARROW_UP = "up"
ARROW_DOWN = "down"

JUMP_COOLDOWN = 0.55       # мин. пауза между прыжками (сек)
JUMP_SAME_OBSTACLE_COOLDOWN = 1.2  # не прыгать повторно на то же препятствие (сек)
MOVE_MIN_HOLD = 0.06       # мин. время удержания клавиши движения (сек)
MOVE_MAX_HOLD = 0.85       # макс. время удержания (защита от залипания)
KEY_REPRESS_INTERVAL = 0.0 # 0 = удерживать; >0 = периодически передёргивать
DRY_RUN = False            # True — не нажимать реальные клавиши (только лог)

# --------------------------------------------------------------------------
# 9. GAME OVER
# --------------------------------------------------------------------------
GAME_OVER_LOST_FRAMES = 60       # игрок не найден столько кадров -> DEAD
GAME_OVER_STATIC_FRAMES = 90     # сцена не меняется столько кадров -> DEAD
GAME_OVER_DIFF_THRESHOLD = 2.0   # средняя разница кадров ниже -> "статика"
GAME_OVER_DARK_V_MEAN = 18       # очень тёмный экран -> вероятно, GAME OVER
RESTART_KEY = "r"                # клавиша перезапуска игры (отправляется в игру)
AUTO_RESTART = False             # автоматически перезапускать после смерти

# --------------------------------------------------------------------------
# 10. ГОРЯЧИЕ КЛАВИШИ
# --------------------------------------------------------------------------
HOTKEY_START_PAUSE = "f6"
HOTKEY_STOP = "f7"
HOTKEY_DEBUG = "f8"
HOTKEY_EMERGENCY = "esc"

START_DELAY = 5            # секунд на переключение в окно игры

# --------------------------------------------------------------------------
# Сохранение / загрузка config.json
# --------------------------------------------------------------------------
# Ключи, которые сохраняются в config.json
PERSISTENT_KEYS = [
    "GAME_REGION",
    "TARGET_FPS",
    "DEBUG_FPS",
    "PROCESS_SCALE",
    "BLUR_KERNEL",
    "DEBUG_MODE",
    "DEBUG_SHOW_MASKS",
    "PLAYER_HSV_LOWER",
    "PLAYER_HSV_UPPER",
    "PLAYER_HSV_EXTRA_RANGES",
    "OBSTACLE_HSV_LOWER",
    "OBSTACLE_HSV_UPPER",
    "OBSTACLE_HSV_EXTRA_RANGES",
    "TRACK_HSV_LOWER",
    "TRACK_HSV_UPPER",
    "USE_TRACK_MASK",
    "PLAYER_MIN_AREA_RATIO",
    "PLAYER_MAX_AREA_RATIO",
    "PLAYER_SEARCH_TOP_RATIO",
    "PLAYER_MAX_ASPECT",
    "OBSTACLE_MIN_AREA_RATIO",
    "OBSTACLE_MAX_AREA_RATIO",
    "OBSTACLE_MIN_WIDTH_RATIO",
    "OBSTACLE_MIN_HEIGHT_RATIO",
    "OBSTACLE_SEARCH_BOTTOM_RATIO",
    "OBSTACLE_MERGE_DISTANCE_RATIO",
    "MORPH_KERNEL",
    "PLAYER_MEMORY_FRAMES",
    "PLAYER_WIDTH",
    "OBSTACLE_MARGIN",
    "SAFE_DISTANCE",
    "REACTION_DISTANCE",
    "JUMP_DISTANCE",
    "JUMP_TIME_AHEAD",
    "LANE_TOLERANCE",
    "CENTER_X_RATIO",
    "SIDE_SCAN_WIDTH_RATIO",
    "EDGE_MARGIN_RATIO",
    "JUMPABLE_HEIGHT_RATIO",
    "UNAVOIDABLE_WIDTH_RATIO",
    "TRACK_MATCH_DISTANCE_RATIO",
    "TRACK_MAX_MISSES",
    "TRACK_SPEED_SMOOTHING",
    "PREDICTION_TIME",
    "MIN_SPEED_FOR_PREDICTION",
    "KEY_LEFT",
    "KEY_RIGHT",
    "KEY_UP",
    "KEY_DOWN",
    "KEY_JUMP",
    "USE_ARROW_KEYS",
    "JUMP_COOLDOWN",
    "JUMP_SAME_OBSTACLE_COOLDOWN",
    "MOVE_MIN_HOLD",
    "MOVE_MAX_HOLD",
    "KEY_REPRESS_INTERVAL",
    "DRY_RUN",
    "GAME_OVER_LOST_FRAMES",
    "GAME_OVER_STATIC_FRAMES",
    "GAME_OVER_DIFF_THRESHOLD",
    "GAME_OVER_DARK_V_MEAN",
    "RESTART_KEY",
    "AUTO_RESTART",
    "START_DELAY",
]

# Снимок значений "по умолчанию" (до применения config.json)
_DEFAULTS: Dict[str, Any] = {}


def _snapshot_defaults() -> None:
    g = globals()
    for key in PERSISTENT_KEYS:
        if key in g:
            _DEFAULTS[key] = copy.deepcopy(g[key])


_snapshot_defaults()


def as_dict() -> Dict[str, Any]:
    """Текущие сохраняемые настройки в виде словаря."""
    g = globals()
    return {key: copy.deepcopy(g[key]) for key in PERSISTENT_KEYS if key in g}


def defaults() -> Dict[str, Any]:
    """Значения по умолчанию (из этого файла)."""
    return copy.deepcopy(_DEFAULTS)


def apply(data: Dict[str, Any]) -> List[str]:
    """
    Применить словарь настроек к модулю.
    Возвращает список применённых ключей.
    """
    g = globals()
    applied: List[str] = []
    for key, value in (data or {}).items():
        if key not in PERSISTENT_KEYS:
            continue
        default_value = _DEFAULTS.get(key)
        if isinstance(default_value, dict) and isinstance(value, dict):
            merged = copy.deepcopy(default_value)
            merged.update(value)
            g[key] = merged
        else:
            g[key] = value
        applied.append(key)
    return applied


def load(path: str = CONFIG_JSON_PATH, verbose: bool = False) -> bool:
    """Загрузить config.json (если существует) и применить его."""
    if not os.path.exists(path):
        if verbose:
            print(f"[config] {path} не найден — использую значения по умолчанию.")
        return False
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError) as exc:
        print(f"[config] Ошибка чтения {path}: {exc}")
        return False
    applied = apply(data)
    if verbose:
        print(f"[config] Загружено {len(applied)} параметров из {path}")
    return True


def save(path: str = CONFIG_JSON_PATH, verbose: bool = True) -> bool:
    """Сохранить текущие настройки в config.json."""
    try:
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(as_dict(), handle, indent=2, ensure_ascii=False)
    except OSError as exc:
        print(f"[config] Не удалось сохранить {path}: {exc}")
        return False
    if verbose:
        print(f"[config] Настройки сохранены в {path}")
    return True


def reset_to_defaults() -> None:
    """Вернуть значения из config.py (без записи на диск)."""
    apply(defaults())


def set_value(key: str, value: Any) -> bool:
    """Изменить один параметр (используется меню настроек)."""
    if key not in PERSISTENT_KEYS:
        return False
    globals()[key] = value
    return True


def movement_keys() -> Dict[str, str]:
    """Актуальные клавиши управления с учётом USE_ARROW_KEYS."""
    if USE_ARROW_KEYS:
        return {
            "left": ARROW_LEFT,
            "right": ARROW_RIGHT,
            "up": ARROW_UP,
            "down": ARROW_DOWN,
            "jump": KEY_JUMP,
        }
    return {
        "left": KEY_LEFT,
        "right": KEY_RIGHT,
        "up": KEY_UP,
        "down": KEY_DOWN,
        "jump": KEY_JUMP,
    }

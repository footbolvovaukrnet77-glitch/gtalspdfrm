"""
selftest.py — самопроверка проекта без запуска игры.

Проверяет на синтетических кадрах:
  * импорт всех модулей;
  * загрузку/сохранение config.json;
  * детекцию игрока и препятствий;
  * трекинг и оценку скорости;
  * логику уклонения и прыжка;
  * конечный автомат;
  * KeyboardController (dry-run): взаимоисключение A/D, кулдаун прыжка,
    освобождение клавиш при исключении;
  * детектор GAME OVER.

Запуск:  python selftest.py      или   python main.py --selftest
"""

from __future__ import annotations

import os
import sys
import traceback
from typing import Callable, List, Optional, Tuple

import numpy as np

import config

RESULTS: List[Tuple[str, bool, str]] = []


def check(name: str) -> Callable:
    """Декоратор: выполнить проверку и записать результат."""

    def decorator(func: Callable[[], None]) -> Callable[[], bool]:
        def wrapper() -> bool:
            try:
                func()
            except AssertionError as exc:
                RESULTS.append((name, False, str(exc) or "assert failed"))
                return False
            except Exception as exc:  # noqa: BLE001
                RESULTS.append((name, False, f"{type(exc).__name__}: {exc}"))
                traceback.print_exc()
                return False
            RESULTS.append((name, True, ""))
            return True

        return wrapper

    return decorator


# ----------------------------------------------------------------------
# Синтетическая сцена
# ----------------------------------------------------------------------
def make_frame(
    width: int = 800,
    height: int = 600,
    player_x: Optional[int] = None,
    obstacle_x: Optional[int] = None,
    obstacle_y: int = 200,
    obstacle_w: int = 160,
    obstacle_h: int = 40,
    with_player: bool = True,
    with_obstacle: bool = True,
) -> np.ndarray:
    """
    Кадр, похожий на игру: тёмный фон, зелёные линии дорожки,
    светлый робот внизу, розовая планка с голубыми стойками.
    """
    import cv2

    frame = np.full((height, width, 3), 18, dtype=np.uint8)

    # Зелёные линии дорожки (перспектива)
    for offset in (-1, 1):
        top_x = int(width / 2 + offset * width * 0.06)
        bottom_x = int(width / 2 + offset * width * 0.42)
        cv2.line(frame, (top_x, int(height * 0.25)), (bottom_x, height), (60, 220, 60), 3)
    for row in range(6):
        y = int(height * (0.3 + row * 0.12))
        cv2.line(frame, (int(width * 0.2), y), (int(width * 0.8), y), (40, 160, 40), 1)

    if with_player:
        px = int(width * 0.5 if player_x is None else player_x)
        py = int(height * 0.85)
        cv2.rectangle(frame, (px - 22, py - 26), (px + 22, py + 26), (215, 215, 215), -1)
        cv2.rectangle(frame, (px - 10, py - 16), (px + 10, py - 4), (240, 240, 240), -1)

    if with_obstacle:
        ox = int(width * 0.5 if obstacle_x is None else obstacle_x)
        # Розовая горизонтальная планка (BGR для HSV ~ 160)
        cv2.rectangle(
            frame,
            (ox - obstacle_w // 2, obstacle_y),
            (ox + obstacle_w // 2, obstacle_y + obstacle_h),
            (190, 60, 240),
            -1,
        )
        # Голубые вертикальные стойки
        for side in (-1, 1):
            sx = ox + side * (obstacle_w // 2 - 8)
            cv2.rectangle(
                frame,
                (sx - 7, obstacle_y - 18),
                (sx + 7, obstacle_y + obstacle_h + 18),
                (240, 200, 60),
                -1,
            )
    return frame


# ----------------------------------------------------------------------
# Проверки
# ----------------------------------------------------------------------
@check("1. Импорт всех модулей проекта")
def test_imports() -> None:
    modules = [
        "config",
        "vision_utils",
        "screen_capture",
        "player_detector",
        "obstacle_detector",
        "tracker",
        "game_state",
        "decision_engine",
        "keyboard_controller",
        "debug_view",
        "calibration",
        "hotkeys",
        "main",
    ]
    import importlib

    for module in modules:
        importlib.import_module(module)


@check("2. config: load / save / reset")
def test_config() -> None:
    original = config.as_dict()
    tmp_path = os.path.join(config.BASE_DIR, "_selftest_config.json")
    try:
        assert config.save(tmp_path, verbose=False), "config.save вернул False"
        config.REACTION_DISTANCE = 12345
        assert config.load(tmp_path), "config.load вернул False"
        assert config.REACTION_DISTANCE == original["REACTION_DISTANCE"], (
            "значение не восстановилось из файла"
        )
        config.reset_to_defaults()
        assert config.GAME_REGION["width"] > 0
    finally:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)
        config.apply(original)


@check("3. Детекция игрока (detect_player)")
def test_player_detection() -> None:
    from player_detector import PlayerDetector

    detector = PlayerDetector()
    frame = make_frame(player_x=400)
    player = detector.detect(frame)
    assert player is not None, "игрок не найден"
    assert abs(player.center_x - 400) < 45, f"center_x={player.center_x}, ожидалось ~400"
    assert player.center_y > frame.shape[0] * 0.6, "игрок найден не в нижней части кадра"
    assert player.width > 10 and player.height > 10, "слишком маленький bbox"

    # Память последнего положения: игрока нет — возвращаем прошлую позицию
    empty = make_frame(with_player=False, with_obstacle=False)
    remembered = detector.detect(empty)
    assert remembered is not None, "не сработала память последнего положения"
    assert detector.detected_this_frame is False


@check("4. Детекция препятствий (detect_obstacles)")
def test_obstacle_detection() -> None:
    from obstacle_detector import ObstacleDetector
    from player_detector import PlayerDetector

    frame = make_frame(player_x=400, obstacle_x=400, obstacle_y=220)
    player = PlayerDetector().detect(frame)
    obstacles = ObstacleDetector().detect(frame, player)
    assert obstacles, "препятствия не найдены"
    main_obstacle = max(obstacles, key=lambda o: o.width * o.height)
    assert abs(main_obstacle.center_x - 400) < 60, f"center_x={main_obstacle.center_x}"
    assert 150 < main_obstacle.center_y < 320, f"center_y={main_obstacle.center_y}"
    # Зелёная дорожка не должна попадать в препятствия
    for obstacle in obstacles:
        assert obstacle.width < frame.shape[1] * 0.9, "детектор поймал всю дорожку"


@check("5. Трекинг: сопоставление и оценка скорости")
def test_tracker() -> None:
    from obstacle_detector import ObstacleDetector
    from player_detector import PlayerDetector
    from tracker import ObstacleTracker

    player_detector = PlayerDetector()
    obstacle_detector = ObstacleDetector()
    tracker = ObstacleTracker()

    now = 1000.0
    dt = 1.0 / 30.0
    player = None
    for step in range(8):
        frame = make_frame(player_x=400, obstacle_x=400, obstacle_y=120 + step * 15)
        player = player_detector.detect(frame)
        obstacles = obstacle_detector.detect(frame, player)
        tracks = tracker.update(obstacles, player, frame.shape[:2], now=now + step * dt)
        assert tracks, f"на шаге {step} нет треков"

    speeds = [t.speed_y for t in tracker.active_tracks() if t.age > 2]
    assert speeds, "нет треков с историей"
    assert max(speeds) > 100, f"скорость не оценена: {speeds}"
    nearest = tracker.nearest_ahead(player)
    assert nearest is not None, "nearest_ahead вернул None"
    assert nearest.distance_y > 0, "distance_y должен быть > 0 для препятствия впереди"
    assert nearest.relative_position in ("left", "right", "center")


@check("6. Решение: уклонение в сторону")
def test_decision_avoid() -> None:
    from decision_engine import ACTION_LEFT, ACTION_RIGHT, DecisionEngine
    from obstacle_detector import ObstacleDetector
    from player_detector import PlayerDetector
    from tracker import ObstacleTracker

    player_detector = PlayerDetector()
    obstacle_detector = ObstacleDetector()
    tracker = ObstacleTracker()
    engine = DecisionEngine()

    decision = None
    now = 2000.0
    for step in range(10):
        frame = make_frame(
            player_x=400, obstacle_x=400, obstacle_y=300 + step * 12, obstacle_w=170
        )
        player = player_detector.detect(frame)
        obstacles = obstacle_detector.detect(frame, player)
        tracker.update(obstacles, player, frame.shape[:2], now=now + step / 30.0)
        decision = engine.decide(player, tracker, frame.shape[:2], now=now + step / 30.0)

    assert decision is not None
    assert decision.action in (ACTION_LEFT, ACTION_RIGHT), (
        f"ожидалось уклонение, получено {decision.action} ({decision.reason})"
    )
    assert decision.threat is not None, "угроза не определена"
    assert decision.target_x is not None, "не задана целевая точка"


@check("7. Решение: прыжок, когда обойти нельзя")
def test_decision_jump() -> None:
    from decision_engine import ACTION_JUMP, DecisionEngine
    from obstacle_detector import ObstacleDetector
    from player_detector import PlayerDetector
    from tracker import ObstacleTracker

    player_detector = PlayerDetector()
    obstacle_detector = ObstacleDetector()
    tracker = ObstacleTracker()
    engine = DecisionEngine()

    decision = None
    now = 3000.0
    for step in range(12):
        # Очень широкая низкая планка — обойти сбоку невозможно
        frame = make_frame(
            player_x=400,
            obstacle_x=400,
            obstacle_y=330 + step * 10,
            obstacle_w=700,
            obstacle_h=26,
        )
        player = player_detector.detect(frame)
        obstacles = obstacle_detector.detect(frame, player)
        tracker.update(obstacles, player, frame.shape[:2], now=now + step / 30.0)
        decision = engine.decide(player, tracker, frame.shape[:2], now=now + step / 30.0)
        if decision.action == ACTION_JUMP:
            break

    assert decision is not None
    assert decision.action == ACTION_JUMP, (
        f"ожидался прыжок, получено {decision.action} ({decision.reason})"
    )


@check("8. Возврат к центру после манёвра")
def test_decision_center() -> None:
    from decision_engine import ACTION_CENTER_LEFT, DecisionEngine
    from player_detector import PlayerDetector
    from tracker import ObstacleTracker

    engine = DecisionEngine()
    tracker = ObstacleTracker()
    frame = make_frame(player_x=620, with_obstacle=False)
    player = PlayerDetector().detect(frame)
    assert player is not None
    decision = engine.decide(player, tracker, frame.shape[:2])
    assert decision.action == ACTION_CENTER_LEFT, (
        f"ожидался возврат влево, получено {decision.action}"
    )
    assert decision.target_x == int(frame.shape[1] * config.CENTER_X_RATIO)


@check("9. Конечный автомат: состояния и переходы")
def test_state_machine() -> None:
    from game_state import ALLOWED_TRANSITIONS, State, StateMachine

    for state in State:
        assert isinstance(state.value, str)
    sm = StateMachine()
    assert sm.state == State.SEARCHING
    assert sm.transition(State.AVOID_LEFT) is True
    assert sm.state == State.AVOID_LEFT
    assert sm.transition(State.RECOVERING) is True
    assert sm.transition(State.MOVING_CENTER) is True
    assert sm.transition(State.PAUSED) is True, "PAUSED должен быть доступен всегда"
    assert sm.transition(State.AVOID_LEFT) is False, "из PAUSED нельзя сразу в AVOID_LEFT"
    assert sm.transition(State.SEARCHING) is True
    assert sm.transition(State.DEAD) is True
    assert sm.transition(State.SEARCHING) is True
    assert set(ALLOWED_TRANSITIONS.keys()) == set(State)


@check("10. KeyboardController: A/D не зажаты вместе, кулдаун прыжка")
def test_keyboard() -> None:
    from keyboard_controller import KeyboardController

    keyboard = KeyboardController(dry_run=True)
    keys = config.movement_keys()

    keyboard.press_left()
    assert keys["left"] in keyboard.held_keys
    keyboard.press_right()
    assert keys["right"] in keyboard.held_keys
    assert keys["left"] not in keyboard.held_keys, "A и D зажаты одновременно!"

    keyboard.stop_horizontal()
    assert not keyboard.held_keys, "клавиши движения не отпущены"

    assert keyboard.jump() is True, "первый прыжок не выполнен"
    assert keyboard.jump() is False, "прыжок сработал раньше кулдауна"

    keyboard.press_left()
    keyboard.release_all()
    assert not keyboard.held_keys, "release_all не отпустил клавиши"


@check("11. Клавиши освобождаются при исключении (try/finally)")
def test_keyboard_exception_safety() -> None:
    from keyboard_controller import KeyboardController

    keyboard = KeyboardController(dry_run=True)
    raised = False
    try:
        with keyboard:
            keyboard.press_right()
            raise RuntimeError("искусственный сбой")
    except RuntimeError:
        raised = True
    assert raised, "исключение не было поднято"
    assert not keyboard.held_keys, "после исключения клавиши остались зажатыми"

    # Защита от бесконечного удержания
    keyboard.press_left()
    keyboard.update(now=__import__("time").time() + config.MOVE_MAX_HOLD + 1.0)
    assert not keyboard.held_keys, "не сработала защита MOVE_MAX_HOLD"
    keyboard.release_all()


@check("12. Детектор GAME OVER")
def test_game_over() -> None:
    from game_state import GameOverDetector

    detector = GameOverDetector()
    frame = make_frame()
    for _ in range(int(config.GAME_OVER_LOST_FRAMES) + 2):
        dead = detector.update(frame, player_found=False)
    assert dead is True, "GAME OVER не обнаружен при потере игрока"

    detector.reset()
    assert detector.update(frame, player_found=True) is False
    assert detector.lost_frames == 0


@check("13. Захват экрана: API и предобработка")
def test_capture_api() -> None:
    import screen_capture

    try:
        capture = screen_capture.ScreenCapture({"left": 0, "top": 0, "width": 320, "height": 240})
    except Exception as exc:  # noqa: BLE001
        raise AssertionError(f"ScreenCapture не создался: {exc}") from exc

    assert capture.backend in ("mss", "pyautogui", "none")
    frame = make_frame(width=320, height=240)
    processed = capture.preprocess(frame)
    assert processed.shape[2] == 3
    capture.close()

    bad = False
    try:
        screen_capture.ScreenCapture({"left": 0, "top": 0, "width": 0, "height": 100})
    except ValueError:
        bad = True
    assert bad, "нулевая ширина области должна вызывать ValueError"


@check("14. requirements.txt существует и не пуст")
def test_requirements() -> None:
    path = os.path.join(config.BASE_DIR, "requirements.txt")
    assert os.path.exists(path), "requirements.txt не найден"
    with open(path, "r", encoding="utf-8") as handle:
        content = [
            line.strip()
            for line in handle
            if line.strip() and not line.strip().startswith("#")
        ]
    assert content, "requirements.txt пуст"
    packages = " ".join(content).lower()
    for package in ("numpy", "opencv", "mss", "pyautogui"):
        assert package in packages, f"в requirements.txt нет {package}"


@check("15. Отрисовка debug-кадра")
def test_debug_view() -> None:
    from debug_view import DebugView
    from decision_engine import DecisionEngine
    from obstacle_detector import ObstacleDetector
    from player_detector import PlayerDetector
    from tracker import ObstacleTracker

    frame = make_frame(player_x=400, obstacle_x=430, obstacle_y=280)
    player = PlayerDetector().detect(frame)
    obstacles = ObstacleDetector().detect(frame, player)
    tracker = ObstacleTracker()
    tracks = tracker.update(obstacles, player, frame.shape[:2])
    decision = DecisionEngine().decide(player, tracker, frame.shape[:2])

    view = DebugView()
    canvas = view.render(frame, player, tracks, decision, fps=30.0, extra_lines=["selftest"])
    assert canvas.shape == frame.shape, "debug-кадр изменил размер"
    assert canvas.dtype == frame.dtype
    over = view.draw_game_over(canvas.copy(), "selftest")
    assert over.shape == frame.shape


@check("16. main.py: разбор аргументов и проверка зависимостей")
def test_main_api() -> None:
    import main as main_module

    args = main_module.parse_args(["--run", "--fps", "20", "--dry-run"])
    assert args.run is True and args.fps == 20 and args.dry_run is True
    missing = main_module.check_dependencies(verbose=False)
    assert isinstance(missing, list)


@check("17. Возврат к центру только после прохождения препятствия")
def test_no_early_return() -> None:
    from decision_engine import (
        ACTION_CENTER_LEFT,
        ACTION_CENTER_RIGHT,
        DecisionEngine,
    )
    from keyboard_controller import KeyboardController
    from obstacle_detector import ObstacleDetector
    from player_detector import PlayerDetector
    from tracker import ObstacleTracker

    player_detector = PlayerDetector()
    obstacle_detector = ObstacleDetector()
    tracker = ObstacleTracker()
    engine = DecisionEngine()
    keyboard = KeyboardController(dry_run=True)
    keys = config.movement_keys()

    player_x = 400.0
    obstacle_y = 60
    now = 5000.0
    violations = []
    try:
        for step in range(46):
            now += 1 / 30.0
            obstacle_y += 14
            frame = make_frame(
                player_x=int(player_x), obstacle_x=430, obstacle_y=obstacle_y, obstacle_w=150
            )
            player = player_detector.detect(frame)
            obstacles = obstacle_detector.detect(frame, player)
            tracker.update(obstacles, player, frame.shape[:2], now=now)
            decision = engine.decide(player, tracker, frame.shape[:2], now=now)
            engine.apply(decision, keyboard, player)
            keyboard.update(now)

            # Простая физика: персонаж двигается, пока клавиша зажата
            if keys["left"] in keyboard.held_keys:
                player_x -= 9
            if keys["right"] in keyboard.held_keys:
                player_x += 9
            player_x = max(30.0, min(frame.shape[1] - 30.0, player_x))

            # Пока препятствие не прошло игрока — возвращаться к центру нельзя
            if player is not None and decision.action in (
                ACTION_CENTER_LEFT,
                ACTION_CENTER_RIGHT,
            ):
                blocking = [
                    t
                    for t in tracker.active_tracks()
                    if t.top < player.bottom and abs(t.center_x - player.center_x) < 200
                ]
                if blocking:
                    violations.append((step, decision.action, obstacle_y))

            # Столкновение: препятствие на уровне игрока и пересекается по X
            for track in tracker.active_tracks():
                if player is None:
                    continue
                overlap_y = track.bottom > player.top and track.top < player.bottom
                overlap_x = track.right > player.left and track.left < player.right
                if overlap_y and overlap_x:
                    violations.append((step, "COLLISION", obstacle_y))
    finally:
        keyboard.release_all()

    assert not violations, f"преждевременный возврат/столкновение: {violations[:5]}"


# ----------------------------------------------------------------------
def run_all() -> bool:
    """Выполнить все проверки. Возвращает True, если всё прошло."""
    print("=" * 62)
    print(" САМОТЕСТ ПРОЕКТА game_bot")
    print("=" * 62)

    tests = [
        test_imports,
        test_config,
        test_player_detection,
        test_obstacle_detection,
        test_tracker,
        test_decision_avoid,
        test_decision_jump,
        test_decision_center,
        test_state_machine,
        test_keyboard,
        test_keyboard_exception_safety,
        test_game_over,
        test_capture_api,
        test_requirements,
        test_debug_view,
        test_main_api,
        test_no_early_return,
    ]
    RESULTS.clear()
    for test in tests:
        test()

    print()
    passed = 0
    for name, ok, message in RESULTS:
        mark = "OK  " if ok else "FAIL"
        print(f"[{mark}] {name}" + (f"  -> {message}" if message else ""))
        passed += 1 if ok else 0

    print("-" * 62)
    print(f"Пройдено: {passed}/{len(RESULTS)}")
    if passed != len(RESULTS):
        print("Некоторые проверки не пройдены — смотрите сообщения выше.")
    else:
        print("Все проверки пройдены.")
    return passed == len(RESULTS)


if __name__ == "__main__":
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    sys.exit(0 if run_all() else 1)

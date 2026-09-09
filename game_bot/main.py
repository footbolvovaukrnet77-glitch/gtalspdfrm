#!/usr/bin/env python3
"""
main.py — точка входа бота.

Запуск:
    python main.py            # меню
    python main.py --run      # сразу запустить бота
    python main.py --calibrate
    python main.py --selftest # самопроверка без игры

Горячие клавиши во время работы:
    F6  — старт/пауза
    F7  — остановить бота
    F8  — вкл/выкл DEBUG
    ESC — аварийная остановка (все клавиши отпускаются)
"""

from __future__ import annotations

import argparse
import signal
import sys
import time
import traceback
from typing import List, Optional

import config


def _fatal(message: str, exc: Optional[BaseException] = None) -> None:
    print(f"\n[ОШИБКА] {message}")
    if exc is not None:
        print(f"         {type(exc).__name__}: {exc}")


# ----------------------------------------------------------------------
class GameBot:
    """Главный цикл бота."""

    def __init__(self) -> None:
        # Импортируем здесь, чтобы меню работало даже без numpy/cv2
        import screen_capture
        from debug_view import DebugView
        from decision_engine import DecisionEngine
        from game_state import GameOverDetector, State, StateMachine
        from keyboard_controller import KeyboardController
        from obstacle_detector import ObstacleDetector
        from player_detector import PlayerDetector
        from tracker import ObstacleTracker

        self.State = State
        self.capture = screen_capture.ScreenCapture(config.GAME_REGION)
        self.player_detector = PlayerDetector()
        self.obstacle_detector = ObstacleDetector()
        self.tracker = ObstacleTracker()
        self.state_machine = StateMachine()
        self.engine = DecisionEngine(self.state_machine)
        self.keyboard = KeyboardController()
        self.debug_view = DebugView()
        self.game_over_detector = GameOverDetector()

        self.running = False        # активна ли обработка (пауза = False)
        self.should_exit = False    # выйти из цикла
        self.debug_enabled = bool(config.DEBUG_MODE)
        self.paused_state_saved = None

        self.fps = 0.0
        self._fps_samples: List[float] = []
        self.frames = 0
        self.deaths = 0

    # ------------------------------------------------------------------
    # Управление
    # ------------------------------------------------------------------
    def toggle_pause(self) -> None:
        self.running = not self.running
        if not self.running:
            self.keyboard.release_all()
            self.state_machine.transition(self.State.PAUSED, force=True)
            print("[bot] ПАУЗА (F6 — продолжить)")
        else:
            self.state_machine.transition(self.State.SEARCHING, force=True)
            print("[bot] РАБОТА (F6 — пауза)")

    def stop(self) -> None:
        print("[bot] Остановка (F7)")
        self.running = False
        self.should_exit = True
        self.keyboard.release_all()

    def emergency_stop(self) -> None:
        print("[bot] АВАРИЙНАЯ ОСТАНОВКА (ESC) — отпускаю все клавиши")
        self.running = False
        self.should_exit = True
        self.keyboard.release_all()

    def toggle_debug(self) -> None:
        self.debug_enabled = not self.debug_enabled
        print(f"[bot] DEBUG = {self.debug_enabled}")
        if not self.debug_enabled:
            self.debug_view.close()

    def restart_game(self) -> None:
        """Перезапуск игры после GAME OVER."""
        print("[bot] Перезапуск игры...")
        self.keyboard.release_all()
        self.keyboard.tap_key(config.RESTART_KEY)
        time.sleep(0.4)
        self.player_detector.reset()
        self.tracker.reset()
        self.engine.reset()
        self.game_over_detector.reset()
        self.state_machine.transition(self.State.SEARCHING, force=True)
        self.running = True

    # ------------------------------------------------------------------
    def target_fps(self) -> float:
        fps = config.DEBUG_FPS if self.debug_enabled else config.TARGET_FPS
        return max(1.0, float(fps))

    def _update_fps(self, frame_time: float) -> None:
        if frame_time <= 0:
            return
        self._fps_samples.append(1.0 / frame_time)
        if len(self._fps_samples) > 20:
            self._fps_samples.pop(0)
        self.fps = sum(self._fps_samples) / len(self._fps_samples)

    # ------------------------------------------------------------------
    def run(self, countdown: Optional[int] = None) -> None:
        """Главный цикл. Всегда освобождает клавиши в finally."""
        from hotkeys import HotkeyManager

        hotkeys = HotkeyManager(
            {
                "start_pause": self.toggle_pause,
                "stop": self.stop,
                "debug": self.toggle_debug,
                "emergency": self.emergency_stop,
            }
        )
        backend = hotkeys.start()

        if self.capture.backend == "none":
            _fatal(
                "Нет бэкенда захвата экрана.\n"
                "         Установите:  pip install mss   (или pip install pyautogui pillow)"
            )
            return

        delay = int(config.START_DELAY if countdown is None else countdown)
        if delay > 0:
            print(f"\nБот запустится через {delay} секунд. Переключитесь на окно игры.")
            for remaining in range(delay, 0, -1):
                print(f"  {remaining}...", end="\r", flush=True)
                time.sleep(1)
            print("  Поехали!            ")

        print(
            "Управление: F6 — пауза, F7 — стоп, F8 — debug, ESC — аварийная остановка"
            + ("" if backend != "none" else "\n(в debug-окне: P — пауза, X — стоп, D — debug, ESC — выход)")
        )

        self.running = True
        self.should_exit = False
        self.state_machine.transition(self.State.SEARCHING, force=True)

        try:
            self._loop(hotkeys)
        except KeyboardInterrupt:
            print("\n[bot] Прервано пользователем (Ctrl+C)")
        except Exception as exc:  # noqa: BLE001 - показываем и продолжаем корректный выход
            _fatal("Сбой в главном цикле", exc)
            traceback.print_exc()
        finally:
            # КРИТИЧНО: отпустить все клавиши при любом исходе
            self.keyboard.release_all()
            hotkeys.stop()
            self.debug_view.close()
            self.capture.close()
            print(
                f"\n[bot] Завершено. Кадров: {self.frames}, "
                f"прыжков: {self.keyboard.jump_count}, смертей: {self.deaths}"
            )

    # ------------------------------------------------------------------
    def _loop(self, hotkeys) -> None:
        from decision_engine import Decision

        last_decision = Decision()
        while not self.should_exit:
            started = time.time()

            frame = self.capture.grab()
            if frame is None:
                time.sleep(0.05)
                continue
            frame = self.capture.preprocess(frame)
            self.frames += 1

            if not self.running:
                # На паузе только показываем картинку
                if self.debug_enabled:
                    canvas = self.debug_view.render(
                        frame, self.player_detector.last_player, [], last_decision,
                        self.fps, ["PAUSED — F6 для продолжения"],
                    )
                    hotkeys.handle_window_key(self.debug_view.show(canvas))
                else:
                    time.sleep(0.05)
                self._sleep_to_target(started)
                continue

            player = self.player_detector.detect(frame)
            obstacles = self.obstacle_detector.detect(frame, player)
            tracks = self.tracker.update(obstacles, player, frame.shape[:2])

            # --- GAME OVER --------------------------------------------
            if self.game_over_detector.update(frame, self.player_detector.detected_this_frame):
                self._handle_game_over(frame, hotkeys)
                self._sleep_to_target(started)
                continue

            decision = self.engine.decide(player, self.tracker, frame.shape[:2])
            self.engine.apply(decision, self.keyboard, player)
            self.keyboard.update()
            last_decision = decision

            if self.debug_enabled:
                canvas = self.debug_view.render(
                    frame, player, tracks, decision, self.fps,
                    [self.keyboard.status()],
                )
                key = self.debug_view.show(canvas)
                hotkeys.handle_window_key(key)
                if config.DEBUG_SHOW_MASKS:
                    self.debug_view.show_masks(
                        self.player_detector.last_mask, self.obstacle_detector.last_mask
                    )

            self._sleep_to_target(started)
            self._update_fps(max(1e-4, time.time() - started))

    # ------------------------------------------------------------------
    def _sleep_to_target(self, started: float) -> None:
        budget = 1.0 / self.target_fps()
        remaining = budget - (time.time() - started)
        if remaining > 0:
            time.sleep(remaining)

    # ------------------------------------------------------------------
    def _handle_game_over(self, frame, hotkeys) -> None:
        """Обработка экрана GAME OVER."""
        self.state_machine.transition(self.State.DEAD, force=True)
        self.keyboard.release_all()
        self.deaths += 1
        reason = self.game_over_detector.last_reason
        print(f"\n=== GAME OVER === ({reason})")
        print("  [R]   — начать заново")
        print("  [ESC] — остановить бота")

        if config.AUTO_RESTART:
            time.sleep(1.0)
            self.restart_game()
            return

        self.running = False
        deadline = time.time() + 120.0
        while not self.should_exit and time.time() < deadline:
            frame = self.capture.grab()
            if frame is None:
                time.sleep(0.1)
                continue
            frame = self.capture.preprocess(frame)
            if self.debug_enabled:
                canvas = self.debug_view.draw_game_over(frame.copy(), reason)
                key = self.debug_view.show(canvas)
            else:
                key = 255
                time.sleep(0.05)

            if key == ord("r"):
                self.restart_game()
                return
            if key == 27:
                self.emergency_stop()
                return
            hotkeys.handle_window_key(key)
            if self.running:  # снято с паузы горячей клавишей
                self.game_over_detector.reset()
                return
        # По таймауту — просто продолжаем наблюдение
        self.game_over_detector.reset()
        self.running = True


# ----------------------------------------------------------------------
# МЕНЮ
# ----------------------------------------------------------------------
def check_dependencies(verbose: bool = True) -> List[str]:
    """Вернуть список отсутствующих библиотек."""
    import importlib

    required = {
        "numpy": "numpy",
        "cv2": "opencv-python",
        "mss": "mss",
        "pyautogui": "pyautogui",
    }
    optional = {"pynput": "pynput"}
    missing: List[str] = []

    for module, package in required.items():
        try:
            importlib.import_module(module)
        except Exception:
            missing.append(package)
            if verbose:
                print(f"  [!] Не найдена библиотека {module}  ->  pip install {package}")

    for module, package in optional.items():
        try:
            importlib.import_module(module)
        except Exception:
            if verbose:
                print(f"  [i] (опционально) {module} не найдена -> pip install {package}")
    return missing


def settings_menu() -> None:
    """Простое меню изменения основных параметров."""
    editable = [
        "TARGET_FPS",
        "DEBUG_MODE",
        "DEBUG_SHOW_MASKS",
        "SAFE_DISTANCE",
        "REACTION_DISTANCE",
        "JUMP_DISTANCE",
        "PLAYER_WIDTH",
        "OBSTACLE_MARGIN",
        "LANE_TOLERANCE",
        "JUMP_COOLDOWN",
        "MOVE_MAX_HOLD",
        "USE_ARROW_KEYS",
        "DRY_RUN",
        "AUTO_RESTART",
        "START_DELAY",
    ]
    while True:
        print("\n=== НАСТРОЙКИ ===")
        for index, key in enumerate(editable, start=1):
            print(f"  [{index:2}] {key} = {getattr(config, key)}")
        print("  [ s] Сохранить в config.json")
        print("  [ 0] Назад")
        choice = input("Выбор: ").strip().lower()
        if choice in ("0", "q", ""):
            return
        if choice == "s":
            config.save()
            continue
        if not choice.isdigit() or not (1 <= int(choice) <= len(editable)):
            print("Неизвестный пункт.")
            continue

        key = editable[int(choice) - 1]
        current = getattr(config, key)
        raw = input(f"{key} (текущее {current}), новое значение: ").strip()
        if raw == "":
            continue
        try:
            if isinstance(current, bool):
                value = raw.lower() in ("1", "true", "y", "yes", "да", "on")
            elif isinstance(current, int):
                value = int(float(raw))
            elif isinstance(current, float):
                value = float(raw)
            else:
                value = raw
        except ValueError:
            print("Не удалось разобрать значение.")
            continue
        config.set_value(key, value)
        print(f"{key} = {getattr(config, key)}")


def main_menu() -> None:
    while True:
        print(
            "\n==============================\n"
            "   GAME BOT — главное меню\n"
            "==============================\n"
            f"  Область игры: {config.GAME_REGION}\n"
            f"  DEBUG_MODE:   {config.DEBUG_MODE}   TARGET_FPS: {config.TARGET_FPS}\n"
            "\n"
            "  [1] Запустить бота\n"
            "  [2] Калибровка\n"
            "  [3] Настройки\n"
            "  [4] Выход\n"
        )
        choice = input("Выбор: ").strip()
        if choice == "1":
            start_bot()
        elif choice == "2":
            try:
                import calibration

                calibration.run()
            except Exception as exc:  # noqa: BLE001
                _fatal("Калибровка недоступна", exc)
        elif choice == "3":
            settings_menu()
        elif choice in ("4", "0", "q", "exit"):
            print("Выход.")
            return
        else:
            print("Неизвестный пункт меню.")


def start_bot() -> None:
    """Создать и запустить бота с гарантированным освобождением клавиш."""
    missing = check_dependencies(verbose=True)
    if missing:
        print(
            "\nНе хватает библиотек. Установите их командой:\n"
            f"    pip install {' '.join(missing)}\n"
        )
        return

    bot = None
    try:
        bot = GameBot()
        bot.run()
    except Exception as exc:  # noqa: BLE001
        _fatal("Не удалось запустить бота", exc)
        traceback.print_exc()
    finally:
        if bot is not None:
            bot.keyboard.release_all()


# ----------------------------------------------------------------------
def install_signal_handlers() -> None:
    """Освободить клавиши при SIGINT/SIGTERM."""

    def handler(signum, frame):  # pragma: no cover - зависит от ОС
        print(f"\n[bot] Сигнал {signum} — освобождаю клавиши и выхожу.")
        try:
            from keyboard_controller import KeyboardController

            KeyboardController().release_all()
        except Exception:
            pass
        sys.exit(1)

    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            signal.signal(sig, handler)
        except Exception:  # pragma: no cover
            pass


def parse_args(argv: Optional[List[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Бот для браузерной игры-бегалки")
    parser.add_argument("--run", action="store_true", help="сразу запустить бота")
    parser.add_argument("--calibrate", action="store_true", help="открыть калибровку")
    parser.add_argument("--selftest", action="store_true", help="самопроверка проекта")
    parser.add_argument("--no-debug", action="store_true", help="выключить debug-окно")
    parser.add_argument("--debug", action="store_true", help="включить debug-окно")
    parser.add_argument("--dry-run", action="store_true", help="не нажимать клавиши")
    parser.add_argument("--fps", type=int, default=None, help="переопределить TARGET_FPS")
    return parser.parse_args(argv)


def main(argv: Optional[List[str]] = None) -> int:
    args = parse_args(argv)
    config.load(verbose=True)

    if args.no_debug:
        config.DEBUG_MODE = False
    if args.debug:
        config.DEBUG_MODE = True
    if args.dry_run:
        config.DRY_RUN = True
    if args.fps:
        config.TARGET_FPS = args.fps

    install_signal_handlers()

    if args.selftest:
        import selftest

        return 0 if selftest.run_all() else 1

    if args.calibrate:
        import calibration

        calibration.run()
        return 0

    if args.run:
        start_bot()
        return 0

    try:
        main_menu()
    except (KeyboardInterrupt, EOFError):
        print("\nВыход.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

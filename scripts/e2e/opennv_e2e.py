#!/usr/bin/env python3
"""End-to-end gameplay tests for OpenNV on macOS, driven through the live harness.

Each test launches the runtime with --live-harness, drives it with ordinary
input (buttons, keys, look, the campaign/route bot) and asserts on the
published live state and the runtime log. Saves are always written to a
temporary directory; the user's own saves are only ever copied, never written.

Usage:
  scripts/e2e/opennv_e2e.py                 # run the default suite
  scripts/e2e/opennv_e2e.py boot radio      # run selected tests
  scripts/e2e/opennv_e2e.py --list
Environment overrides: OPENNV_E2E_GODOT, OPENNV_E2E_FNV, OPENNV_E2E_FO3,
OPENNV_E2E_MODS, OPENNV_E2E_SAVE (save to copy for continue-based tests).
"""
from __future__ import annotations

import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
HOME = Path.home()
GODOT = Path(os.environ.get("OPENNV_E2E_GODOT",
    "/Applications/OpenNV.app/Contents/Resources/Godot_mono.app/Contents/MacOS/Godot"))
FNV = Path(os.environ.get("OPENNV_E2E_FNV", HOME / "Games/FalloutNV"))
FO3 = Path(os.environ.get("OPENNV_E2E_FO3", HOME / "Games/Fallout3"))
MODS = Path(os.environ.get("OPENNV_E2E_MODS", HOME / "Games/FalloutNV-Mods"))
SAVE = Path(os.environ.get("OPENNV_E2E_SAVE", HOME / "Games/OpenNVSaves/newvegas-allmods/courier-v1.json"))
ALL_MODS = [("yup", "YUP"), ("nmc", "NMC"), ("eve", "EVE"), ("uncut-wasteland", "UncutWasteland"), ("bounties", "Bounties")]
FATAL = ("OPENNV_GODOT_RUNTIME_FAIL", "Unhandled exception", "is missing native code", "DllNotFoundException",
         "native audio extension is not loaded", "OutOfMemoryException")


class Failure(Exception):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise Failure(message)


class Game:
    """One runtime process with its own harness directory, log and saves."""

    def __init__(self, name: str, game: str = "fnv", mods: list[tuple[str, str]] | None = None,
                 save: Path | None = None, new_game: bool = False, extra: list[str] | None = None):
        self.root = Path(tempfile.mkdtemp(prefix=f"opennv-e2e-{name}-"))
        self.harness = self.root / "harness"
        self.harness.mkdir()
        self.log_path = self.root / "runtime.log"
        save_dir = self.root / "saves"
        save_dir.mkdir()
        if save is not None:
            # Copy the save and its slot directory; never write the original.
            shutil.copy2(save, save_dir / save.name)
            slots = Path(str(save) + ".slots-v1")
            if slots.is_dir():
                shutil.copytree(slots, save_dir / slots.name)
            save_path = save_dir / save.name
        else:
            save_path = save_dir / ("vault-v1.json" if game == "fo3" else "courier-v1.json")
        args = [str(GODOT), "--resolution", "1280x720", "--path", str(REPO / "runtime"), "--",
                "--save-path", str(save_path), "--live-harness", str(self.harness)]
        if game == "fo3":
            args += ["--data-root", str(FO3), "--campaign", "fallout-3"]
        else:
            args += ["--data-root", str(FNV), "--campaign", "fallout-new-vegas"]
            if mods:
                stack = [{"Id": mod, "Root": str(MODS / folder), "AdditionalRoots": []} for mod, folder in mods]
                args += ["--mod-stack", json.dumps(stack)]
        if new_game:
            args.append("--new-game")
        args += extra or []
        self.log = open(self.log_path, "w")
        env = dict(os.environ, OPENNV_FRAME_SPIKE_MS=os.environ.get("OPENNV_FRAME_SPIKE_MS", "150"))
        self.process = subprocess.Popen(args, cwd=REPO, stdout=self.log, stderr=subprocess.STDOUT,
                                        env=env, start_new_session=True)

    # -- lifecycle -----------------------------------------------------
    def close(self) -> None:
        if self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                self.process.wait(10)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
        self.log.close()

    def __enter__(self) -> "Game":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    # -- observation -----------------------------------------------------
    def state(self) -> dict:
        for _ in range(20):
            try:
                return json.loads((self.harness / "live-state.json").read_text())
            except (FileNotFoundError, json.JSONDecodeError):
                time.sleep(0.25)
        require(self.process.poll() is None, f"runtime exited with code {self.process.returncode}")
        raise Failure("live state never became readable")

    def log_text(self) -> str:
        self.log.flush()
        return self.log_path.read_text(errors="replace")

    def fatal_errors(self) -> list[str]:
        return [line[:240] for line in self.log_text().splitlines() if any(marker in line for marker in FATAL)]

    def controls(self) -> list[dict]:
        return self.state().get("controls") or []

    def wait(self, predicate, timeout: float, what: str, interval: float = 1.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            require(self.process.poll() is None, f"runtime exited while waiting for {what}")
            try:
                value = predicate()
            except Failure:
                value = None
            if value:
                return value
            time.sleep(interval)
        raise Failure(f"timed out after {timeout:.0f}s waiting for {what}")

    def wait_log(self, marker: str, timeout: float) -> str:
        return self.wait(lambda: next((l for l in self.log_text().splitlines() if marker in l), None), timeout, marker)

    # -- input -------------------------------------------------------------
    def command(self, payload: dict) -> dict:
        request = self.state()["nextCommandRequest"]
        name = f"{request:010d}"
        (self.harness / f"{name}.command").write_text(json.dumps(payload))
        receipt = self.harness / f"{name}.receipt.json"
        self.wait(receipt.exists, 15, f"receipt for {payload.get('op')}", 0.1)
        return json.loads(receipt.read_text())

    def press(self, text: str) -> bool:
        match = next((c for c in self.controls() if c.get("text") == text), None)
        if match is None:
            return False
        self.command({"op": "button", "path": match["path"]})
        return True

    def tap(self, key: str) -> None:
        self.command({"op": "key", "key": key, "pressed": True, "leaseMilliseconds": 150})

    def dismiss_popups(self, quiet_seconds: float = 6) -> int:
        dismissed, quiet = 0, 0.0
        while quiet < quiet_seconds:
            if self.press("OK"):
                dismissed, quiet = dismissed + 1, 0.0
            else:
                quiet += 1
            time.sleep(1)
        return dismissed

    def settle(self, seconds: float) -> None:
        """Wait while dismissing timed DLC/notification popups, as a player would."""
        deadline = time.time() + seconds
        while time.time() < deadline:
            self.press("OK")
            time.sleep(1)

    def gameplay(self) -> dict:
        return self.state().get("gameplay") or {}


# -- tests -------------------------------------------------------------------
def wait_menu(game: Game, timeout: float = 120) -> list[str]:
    return game.wait(lambda: [c["text"] for c in game.controls() if c.get("text") in ("Continue", "New")] or None,
                     timeout, "main menu")


def continue_into_world(game: Game) -> dict:
    wait_menu(game)
    require(game.press("Continue"), "Continue is not offered for the copied save")
    game.wait_log("OPENNV_NATIVE_ACTIVE_CELL", 180)
    game.dismiss_popups()
    def ready():
        state = game.state()
        gameplay = state.get("gameplay") or {}
        return gameplay if gameplay.get("cell") and not state["sceneTreePaused"] else None
    return game.wait(ready, 60, "unpaused gameplay")


def test_boot() -> str:
    """Launches to the main menu with no fatal errors."""
    with Game("boot") as game:
        options = wait_menu(game)
        require(not game.fatal_errors(), f"fatal errors: {game.fatal_errors()[:3]}")
        return f"menu offers {options}"


def test_continue() -> str:
    """Continues a copy of the all-mods save into the world, unpaused."""
    require(SAVE.exists(), f"save to copy is missing: {SAVE}")
    with Game("continue", mods=ALL_MODS, save=SAVE) as game:
        gameplay = continue_into_world(game)
        game.settle(15)
        require(not game.state()["sceneTreePaused"], "game paused unexpectedly after loading")
        require(not game.fatal_errors(), f"fatal errors: {game.fatal_errors()[:3]}")
        return f"loaded cell {gameplay['cell']}"


def open_pipboy(game: Game) -> None:
    """Open the Pip-Boy, retrying around timed DLC popups that take the key."""
    def opened():
        if any(c.get("text") == "DATA" for c in game.controls()):
            return True
        if not game.press("OK"):
            game.tap("Tab")
        time.sleep(2)
        return any(c.get("text") == "DATA" for c in game.controls())
    game.wait(opened, 40, "Pip-Boy", 0.5)


def open_radio(game: Game, station: str) -> None:
    open_pipboy(game)
    for button in ("DATA", "Radio", station):
        require(game.wait(lambda b=button: game.press(b), 10, f"Pip-Boy button {button}"), f"missing {button}")
        time.sleep(1.5)
    game.tap("Tab")


def quicksave(game: Game, attempts: int = 8) -> str:
    """F5, retrying while a transient world condition (e.g. an explosion) blocks saving."""
    result = ""
    for _ in range(attempts):
        mark = len(game.log_text().splitlines())
        game.tap("F5")
        result = game.wait(lambda: next((l for l in game.log_text().splitlines()[mark:]
                                        if "SAVE_SLOT_CREATED" in l or "SAVE_FAILURE" in l), None), 30, "quicksave result")
        if "SAVE_SLOT_CREATED" in result or "transient" not in result:
            return result
        game.settle(5)
    return result


def test_radio() -> str:
    """Tunes Radio New Vegas in the Pip-Boy and checks the broadcast audio advances."""
    require(SAVE.exists(), f"save to copy is missing: {SAVE}")
    with Game("radio", mods=ALL_MODS, save=SAVE) as game:
        continue_into_world(game)
        open_radio(game, "Radio New Vegas")
        game.wait_log("OPENNV_NATIVE_RADIO_TUNE", 10)
        game.wait_log("OPENNV_NATIVE_RADIO_BROADCAST", 30)
        first = game.wait(lambda: (game.gameplay().get("speech") or {}).get("positionSeconds") or None, 30,
                          "radio audio position")
        time.sleep(4)
        later = (game.gameplay().get("speech") or {}).get("positionSeconds") or 0
        require(later != first, "radio audio position is not advancing")
        failures = [l for l in game.log_text().splitlines() if "RADIO_BROADCAST_FAILED" in l or "PIPBOY_ACTION_FAILED" in l]
        require(not failures, f"radio failures: {failures[:2]}")
        # Saving must work while a station plays (quicksave on F5).
        game.dismiss_popups(8)  # DLC story messages queue up after the first minutes
        saved = quicksave(game)
        require("SAVE_SLOT_CREATED" in saved, f"quicksave with the radio on failed: {saved[:200]}")
        require(not game.fatal_errors(), f"fatal errors: {game.fatal_errors()[:3]}")
        return "Radio New Vegas broadcasting with advancing audio; quicksave succeeded"


def test_perf() -> str:
    """Samples 30 s in the world: p99 frame time and main-thread spikes."""
    require(SAVE.exists(), f"save to copy is missing: {SAVE}")
    with Game("perf", mods=ALL_MODS, save=SAVE) as game:
        continue_into_world(game)
        game.settle(15)  # let one-off load work, popups and shader compilation settle
        mark = len(game.log_text().splitlines())
        game.settle(30)
        state = game.state()
        intervals = state["performance"]["hostDrawIntervals"]
        spikes = [l for l in game.log_text().splitlines()[mark:] if "OPENNV_FRAME_SPIKE" in l]
        require(not state["sceneTreePaused"], "game paused during the measurement")
        require(intervals["p99Milliseconds"] < 34, f"p99 frame time {intervals['p99Milliseconds']:.1f} ms")
        require(not spikes, f"main-thread spikes over 150 ms: {spikes[:3]}")
        return (f"median {intervals['medianMilliseconds']:.1f} ms, p99 {intervals['p99Milliseconds']:.1f} ms, "
                f"max {intervals['maximumMilliseconds']:.0f} ms")


def answer_dialogue(game: Game) -> bool:
    conversation = game.gameplay().get("conversation") or {}
    if conversation.get("phase") != "choices":
        return False
    choices = [c["text"] for c in conversation.get("choices") or []]
    preferred = next((c for c in choices if c.startswith("Thanks")), choices[0] if choices else None)
    return preferred is not None and game.press(preferred)


def test_opening() -> str:
    """New game from the intro to standing outside Doc Mitchell's house."""
    with Game("opening", new_game=True) as game:
        game.wait_log("OPENNV_NATIVE_ACTIVE_CELL", 180)
        time.sleep(3)
        game.tap("Escape")  # skip the intro movie
        game.command({"op": "campaign", "action": "start", "entry": "new"})
        deadline = time.time() + 900
        tagged: list[str] = []
        while time.time() < deadline:
            gameplay = game.gameplay()
            if gameplay.get("cell") == "FalloutNV.esm:0daebb":
                break
            texts = [c.get("text") for c in game.controls()]
            stage = (gameplay.get("opening") or {}).get("stage") or 0
            if "No (Recommended)" in texts:
                game.press("No (Recommended)")
            elif "Guns" in texts and "Done" in texts:
                for skill in ("Guns", "Speech", "Lockpick"):
                    if skill not in tagged:
                        game.press(skill); tagged.append(skill); time.sleep(0.5)
                game.press("Done")
            elif "Done" in texts and stage >= 100:
                game.press("Done")  # traits
            elif "NEXT" in texts:
                game.press("NEXT")  # appearance (race/face) menu
            elif ok := next((t for t in texts if t and t.lower() == "ok"), None):
                game.press(ok)  # message boxes say OK; name entry says Ok
            elif answer_dialogue(game):
                pass
            elif stage >= 200 and (game.state().get("bot") or {}).get("phase") not in ("awaiting-interaction", "planning"):
                game.command({"op": "bot", "mode": "interact", "reference": "FalloutNV.esm:103e61", "pauseAfter": False})
            elif (game.state().get("campaignBot") or {}).get("phase") == "blocked" and stage >= 80:
                bot = game.state().get("bot") or {}
                if bot.get("phase") not in ("awaiting-interaction", "planning", "approaching"):
                    target = "FalloutNV.esm:103e55" if stage < 90 else "FalloutNV.esm:104c0f"
                    game.command({"op": "bot", "mode": "interact", "reference": target, "pauseAfter": False})
            time.sleep(2)
        require(game.gameplay().get("cell") == "FalloutNV.esm:0daebb", "did not reach Goodsprings")
        game.dismiss_popups()
        require(not game.fatal_errors(), f"fatal errors: {game.fatal_errors()[:3]}")
        return "reached Goodsprings from a new game"


def test_fo3() -> str:
    """Fallout 3 new game through character creation and the birth scene."""
    require(FO3.exists(), f"Fallout 3 is missing: {FO3}")
    with Game("fo3", game="fo3", new_game=True) as game:
        deadline = time.time() + 420
        while time.time() < deadline:
            if "quest=Fallout3.esm:01f388 stage=100" in game.log_text():
                break
            texts = [c.get("text") for c in game.controls()]
            for button in ("Yes", "Boy", "Ok", "NEXT", "DONE", "OK"):
                if button in texts:
                    game.press(button)
                    break
            time.sleep(2)
        require("quest=Fallout3.esm:01f388 stage=100" in game.log_text(), "birth scene did not reach stage 100")
        require(not game.fatal_errors(), f"fatal errors: {game.fatal_errors()[:3]}")
        return "birth scene reached stage 100"


TESTS = {"boot": test_boot, "continue": test_continue, "radio": test_radio, "perf": test_perf,
         "opening": test_opening, "fo3": test_fo3}
DEFAULT = ["boot", "continue", "radio", "perf", "opening", "fo3"]


def main(argv: list[str]) -> int:
    if "--list" in argv:
        print("\n".join(f"{name}: {(fn.__doc__ or '').strip()}" for name, fn in TESTS.items()))
        return 0
    selected = [a for a in argv if not a.startswith("-")] or DEFAULT
    unknown = [name for name in selected if name not in TESTS]
    if unknown:
        print(f"unknown tests: {unknown}", file=sys.stderr)
        return 2
    results = []
    for name in selected:
        started = time.time()
        try:
            detail, passed = TESTS[name](), True
        except Failure as error:
            detail, passed = str(error), False
        except Exception as error:  # infrastructure failure, reported as a failure
            detail, passed = f"{type(error).__name__}: {error}", False
        results.append((name, passed, detail, time.time() - started))
        print(f"{'PASS' if passed else 'FAIL'} {name:<9} {time.time() - started:6.0f}s  {detail}", flush=True)
    failed = [r for r in results if not r[1]]
    print(f"\n{len(results) - len(failed)}/{len(results)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

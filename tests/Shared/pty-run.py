#!/usr/bin/env python3
"""Run a command on a pseudo-terminal, drive it on a schedule, and report what happened as JSON.

    pty-run.py [options] -- <command> [args...]

For the screen's facts (R10.72, R10.73): a screen is only a screen on a terminal, and what it must do when a
key, a signal, a resize or a hang-up arrives is only observable from the other side of one. The command is run as
the session leader of a new pseudo-terminal, and closing the master hangs the session up, as a terminal window closing
does. Ctrl-C, Ctrl-\\ and Ctrl-Z are signals the line discipline raises only while the terminal's input mode is cooked;
a screen that puts it in a raw mode (curia browse does) receives them as the bytes 03, 1c and 1a, which is how these
tests send them (--send 03@0), and handles them itself.

Options (times are milliseconds after the screen is up, that is after --ready text first appears in the output):

    --size COLSxROWS          initial window size (default 80x24)
    --env NAME=VALUE          set an environment variable (repeatable)
    --ready TEXT              output that means the first frame is up (default: q quits)
    --alive-ms N              after the screen is up, wait N ms and record whether the child is still running
    --send HEX@MS             write these bytes to the terminal (repeatable)
    --signal NAME@MS          send a signal to the child, e.g. TERM@0 (repeatable)
    --resize COLSxROWS@MS     change the window size and send SIGWINCH (repeatable)
    --close-master-at MS      close the master side, hanging the session up
    --timeout S               kill the child and report "timeout" after S seconds (default 30)

Prints one JSON object on stdout:

    {"status": "exited"|"signaled"|"timeout", "exit": int|null, "signal": int|null,
     "ready_ms": number|null, "alive_before_actions": bool|null, "exited_ms": number|null,
     "chunks": [[ms, "<hex>"], ...], "output_hex": "<hex>"}

`exited_ms` and each chunk's time are milliseconds after the screen was up (negative before it). Nothing here is
written to disk, and nothing executable is created: the command is a path the caller names.
"""

import argparse
import fcntl
import json
import os
import pty
import select
import signal
import struct
import sys
import termios
import time


def set_size(fd: int, cols: int, rows: int) -> None:
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))


def parse_size(text: str) -> tuple[int, int]:
    cols, rows = text.lower().split("x")
    return int(cols), int(rows)


def parse_timed(text: str) -> tuple[str, float]:
    value, at = text.rsplit("@", 1)
    return value, float(at)


def main() -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--size", default="80x24")
    parser.add_argument("--env", action="append", default=[])
    parser.add_argument("--ready", default="q quits")
    parser.add_argument("--alive-ms", type=float, default=None)
    parser.add_argument("--send", action="append", default=[])
    parser.add_argument("--signal", action="append", default=[])
    parser.add_argument("--resize", action="append", default=[])
    parser.add_argument("--close-master-at", type=float, default=None)
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    options = parser.parse_args()

    command = options.command[1:] if options.command[:1] == ["--"] else options.command
    if not command:
        print("usage: pty-run.py [options] -- <command> [args...]", file=sys.stderr)
        return 2

    environment = dict(os.environ)
    for pair in options.env:
        name, value = pair.split("=", 1)
        environment[name] = value

    cols, rows = parse_size(options.size)

    actions = []  # (ms, kind, argument)
    for item in options.send:
        hex_bytes, at = parse_timed(item)
        actions.append((at, "send", bytes.fromhex(hex_bytes)))
    for item in options.signal:
        name, at = parse_timed(item)
        actions.append((at, "signal", getattr(signal, "SIG" + name.upper())))
    for item in options.resize:
        size, at = parse_timed(item)
        actions.append((at, "resize", parse_size(size)))
    if options.close_master_at is not None:
        actions.append((options.close_master_at, "close", None))
    actions.sort(key=lambda a: a[0])

    pid, master = pty.fork()
    if pid == 0:
        try:
            os.execvpe(command[0], command, environment)
        except OSError as error:
            os.write(2, f"pty-run: cannot run {command[0]}: {error}\n".encode())
            os._exit(127)

    set_size(master, cols, rows)

    started = time.monotonic()
    ready_at = None
    alive_before_actions = None
    alive_checked = options.alive_ms is None
    chunks = []
    output = bytearray()
    pending = list(actions)
    master_open = True
    status = None
    exited_at = None
    ready_marker = options.ready.encode()

    def now_ms() -> float:
        return (time.monotonic() - started) * 1000.0

    def relative(ms: float) -> float:
        return ms - ready_at if ready_at is not None else ms

    def reap() -> bool:
        nonlocal status, exited_at
        if status is not None:
            return True
        done, wait_status = os.waitpid(pid, os.WNOHANG)
        if done == 0:
            return False
        status = wait_status
        exited_at = now_ms()
        return True

    while status is None:
        elapsed = now_ms()
        if elapsed > options.timeout * 1000.0:
            os.kill(pid, signal.SIGKILL)
            _, wait_status = os.waitpid(pid, 0)
            report = {
                "status": "timeout", "exit": None, "signal": None, "ready_ms": ready_at,
                "alive_before_actions": alive_before_actions, "exited_ms": None,
                "chunks": chunks, "output_hex": bytes(output).hex(),
            }
            print(json.dumps(report))
            return 0

        if ready_at is not None and not alive_checked and relative(elapsed) >= options.alive_ms:
            alive_before_actions = not reap()
            alive_checked = True

        if ready_at is not None and alive_checked and pending and relative(elapsed) >= pending[0][0]:
            _, kind, argument = pending.pop(0)
            try:
                if kind == "send":
                    os.write(master, argument)
                elif kind == "signal":
                    os.kill(pid, argument)
                elif kind == "resize":
                    set_size(master, argument[0], argument[1])
                    os.kill(pid, signal.SIGWINCH)
                elif kind == "close":
                    os.close(master)
                    master_open = False
            except OSError:
                pass
            continue

        if master_open:
            readable, _, _ = select.select([master], [], [], 0.005)
            if readable:
                try:
                    data = os.read(master, 65536)
                except OSError:
                    data = b""
                if data:
                    chunks.append([relative(now_ms()) if ready_at is not None else now_ms() - 0.0, data.hex()])
                    output += data
                    if ready_at is None and ready_marker in output:
                        ready_at = now_ms()
                        # Chunks recorded before the screen was up are made relative to it, so a negative time is "before".
                        for chunk in chunks:
                            chunk[0] = chunk[0] - ready_at
                else:
                    master_open = False
        else:
            time.sleep(0.005)

        reap()

    # The child is gone; collect whatever it wrote last.
    if master_open:
        while True:
            readable, _, _ = select.select([master], [], [], 0.05)
            if not readable:
                break
            try:
                data = os.read(master, 65536)
            except OSError:
                break
            if not data:
                break
            chunks.append([relative(now_ms()), data.hex()])
            output += data
        os.close(master)

    exited = os.WIFEXITED(status)
    report = {
        "status": "exited" if exited else "signaled",
        "exit": os.WEXITSTATUS(status) if exited else None,
        "signal": None if exited else os.WTERMSIG(status),
        "ready_ms": ready_at,
        "alive_before_actions": alive_before_actions,
        "exited_ms": relative(exited_at) if ready_at is not None else None,
        "chunks": chunks,
        "output_hex": bytes(output).hex(),
    }
    print(json.dumps(report))
    return 0


if __name__ == "__main__":
    sys.exit(main())

"""PreToolUse launcher for the environment hook (``shakedown-safety.py``).

Claude Code runs a command whose hook exits with any code other than 2
(a crash, exit 1, a signal) or outlives its registered timeout. This
launcher runs the hook as a child process and turns every end other than a
pass into a refusal:

* exit 0 from the hook: exit 0 (the command runs);
* exit 2 from the hook: exit 2 (the hook has printed its reason);
* any other exit, a signal, or a hook that cannot be started: a refusal
  that names the exit, exit 2;
* no answer within ``_TIMEOUT_SECONDS`` of the launcher's start: the hook
  is killed and the command refused, exit 2.

The hook reads the launcher's own standard input and writes to its
standard output and error (the handles are passed on, nothing is copied),
so the launcher holds no pipe that could block it. It starts no thread,
and it ends with ``os._exit`` after flushing, so nothing runs at
interpreter shutdown.

What this file cannot cover itself, the registered command covers:
``python ".../shakedown-safety-launcher.py" || exit 2`` turns a launcher
that cannot start (no ``python``, a missing file) or that is ended by a
signal into exit 2 as well. What remains is Claude Code's own limit: if the
launcher has not ended when the registered timeout passes (10 s, double
``_TIMEOUT_SECONDS``), Claude Code runs the command.
"""
import os
import subprocess
import sys
import time

_START = time.monotonic()

# The hook answers within its 3.5 s budget, measured from its own start;
# this limit adds the child's start-up and a margin, and stays at half of
# the 10 s the hook is registered with.
_TIMEOUT_SECONDS = 5.0

_HOOK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "shakedown-safety.py")


def _end(code, message=None):
    """Write ``message`` (if any) to standard error and end the process with
    ``code`` at once: no interpreter shutdown, so nothing can run after."""
    try:
        if message:
            sys.stderr.write(message + "\n")
        sys.stderr.flush()
    except BaseException:  # noqa: BLE001 -- the exit code is what counts
        pass
    finally:
        os._exit(code)


def _describe(code):
    if code < 0:
        try:
            import signal
            return "signal %s" % signal.Signals(-code).name
        except (ImportError, ValueError):
            return "signal %d" % -code
    if code > 255:  # a Windows status such as 0xC0000005 (access violation)
        return "status 0x%08X" % (code & 0xFFFFFFFF)
    return "exit code %d" % code


def _refuse(why):
    _end(2, "BLOCKED [shakedown-safety]: %s, so the command is refused." % why)


def _kill(child):
    try:
        child.kill()
        child.wait(timeout=1.0)
    except BaseException:  # noqa: BLE001 -- refused whatever happens here
        pass


def main():
    if not os.path.isfile(_HOOK):
        _refuse("the hook file shakedown-safety.py is missing next to its launcher")
    try:
        child = subprocess.Popen([sys.executable, _HOOK], stdin=0, stdout=1, stderr=2)
    except BaseException as exc:  # noqa: BLE001
        _refuse("the hook could not be started (%s)" % type(exc).__name__)
    try:
        remaining = max(0.0, _TIMEOUT_SECONDS - (time.monotonic() - _START))
        code = child.wait(timeout=remaining)
    except subprocess.TimeoutExpired:
        _kill(child)
        _refuse("the hook did not answer within %g seconds" % _TIMEOUT_SECONDS)
    except BaseException as exc:  # noqa: BLE001 -- an interrupt, or anything else
        _kill(child)
        _refuse("the launcher was interrupted (%s)" % type(exc).__name__)
    if code == 0:
        _end(0)
    if code == 2:
        _end(2)
    _refuse("the hook ended abnormally (%s)" % _describe(code))


if __name__ == "__main__":
    try:
        main()
    finally:
        # main() always ends the process; reaching here means it did not.
        _end(2, "BLOCKED [shakedown-safety]: the launcher failed, so the command is refused.")

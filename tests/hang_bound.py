"""The hang guard for child processes started by Python tests (#1684).

A test that runs a child process (bash, pwsh, python, node) waits for it to exit. The
``timeout`` passed to ``subprocess.run`` only catches a hang; it is not a speed assertion.
Idle start-up of these children is well under a second, but on a loaded hosted runner it can
stretch to many seconds, and a tight wall-clock budget would fail the test with the behaviour
still correct. 300 s is about 1000x the idle time yet still ends a genuinely wedged child well
inside the job timeout. When it fires, ``subprocess.TimeoutExpired`` names the command and the
bound, so the failure says what was awaited.

The .NET tests follow the same rule with ``tests/Shared/HangBackstop.cs``. Import this by bare
name: ``tests/conftest.py`` inserts ``tests/`` into ``sys.path`` in every pytest import mode.
"""

CHILD_PROCESS_HANG_BOUND_SECONDS = 300

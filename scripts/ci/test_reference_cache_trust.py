"""Native cache-location and legacy-cache regression; no Dataverse access."""
import argparse
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile


def PrivateCache_IgnoresLegacySeed(probe: Path, rid: str) -> None:
    """AC-61: same-length poisoned legacy contents remain untouched and unused."""
    with tempfile.TemporaryDirectory(prefix="ppds-cache-probe-") as temporary:
        root = Path(temporary).resolve()
        (root / ".ppds-cache-probe").touch()
        env = dict(os.environ, TEMP=str(root), TMP=str(root), TMPDIR=str(root),
                   PPDS_CACHE_PROBE_ROOT=str(root))

        def run(*args: str) -> str:
            result = subprocess.run(["dotnet", str(probe), *args], env=env,
                                    capture_output=True, text=True, timeout=90, check=True)
            assert not result.stderr, result.stderr
            return result.stdout.strip()

        architecture, _ = run("identity").split(" ", 1)
        assert architecture == rid.split("-")[-1], (architecture, rid)
        legacy = Path(run("seed-legacy"))

        def snapshot() -> dict:
            return {str(p.relative_to(legacy)): (hashlib.sha256(p.read_bytes()).hexdigest(), p.stat().st_mtime_ns)
                    for p in legacy.rglob("*") if p.is_file()}

        before = snapshot()
        private = Path(run("create"))  # Actual SpecialFolder.LocalApplicationData, no override.
        assert "PPDS/cache/net462-ref" in private.as_posix(), private
        assert not private.is_relative_to(root), private
        assert snapshot() == before, "Legacy fixture was changed/deleted"
        timestamp = (private / "System.Runtime.dll").stat().st_mtime_ns
        assert Path(run("create")) == private
        assert (private / "System.Runtime.dll").stat().st_mtime_ns == timestamp
        assert snapshot() == before
        print(f"PASS AC-61/65 {rid}: actual private cache, exact embedded bytes, unchanged legacy fixture and warm reuse")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--rid", required=True)
    arguments = parser.parse_args()
    PrivateCache_IgnoresLegacySeed(arguments.probe.resolve(), arguments.rid)

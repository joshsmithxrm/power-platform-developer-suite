#!/usr/bin/env python3
"""Exercise a published single-file CLI, without Dataverse or installed DLL sidecars."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile


class SingleFileExtractionTests(unittest.TestCase):
    binary_source: Path
    fixture_source: Path
    cache_probe: Path
    reference_archive: Path
    allow_existing_cache: bool

    @classmethod
    def setUpClass(cls):
        # The probe only asks the OS for its actual per-account data directory;
        # it neither prepares the cache nor supplies references to the copied CLI.
        description = subprocess.run(
            ["dotnet", str(cls.cache_probe), "describe"], capture_output=True,
            text=True, encoding="utf-8", timeout=30, check=True,
        )
        if description.stderr.strip():
            raise AssertionError(description.stderr)
        local_data = Path(json.loads(description.stdout)["localApplicationData"])
        if not local_data.is_absolute():
            raise AssertionError("The OS did not provide an absolute local application data directory")
        archive_hash = hashlib.sha256(cls.reference_archive.read_bytes()).hexdigest()[:16]
        with zipfile.ZipFile(cls.reference_archive) as archive:
            cls.expected_references = {
                entry.filename: archive.read(entry) for entry in archive.infolist()
                if not entry.is_dir()
            }
        if not {"mscorlib.dll", "System.Runtime.dll"} <= cls.expected_references.keys():
            raise AssertionError("The published reference archive is incomplete")
        cls.cache = (local_data / "PPDS" / "cache" / "net462-ref" / archive_hash).resolve()
        if cls.cache.exists():
            if not cls.allow_existing_cache:
                raise AssertionError(
                    f"Cold-cache verification requires an absent cache: {cls.cache}. "
                    "Run on a fresh runner, or use --allow-existing-cache for an explicit "
                    "local warm-cache check. Existing account caches are never removed."
                )
            print(f"LOCAL WARM-CACHE CHECK: {cls.cache}; cold creation is not verified.",
                  file=sys.stderr)
        else:
            print(f"Cold-cache verification: {cls.cache}", file=sys.stderr)
        cls.sandbox = tempfile.TemporaryDirectory(prefix="ppds single file ")
        cls.addClassCleanup(cls.sandbox.cleanup)
        cls.root = Path(cls.sandbox.name)
        binary_dir = cls.root / "binary only"
        binary_dir.mkdir()
        cls.binary = binary_dir / cls.binary_source.name
        shutil.copy2(cls.binary_source, cls.binary)

    def setUp(self):
        self.case = self.root / self._testMethodName
        self.case.mkdir()
        self.temp = self.case / "temp"
        self.temp.mkdir()
        self.fixture = self.case / "plugin inputs"
        self.fixture.mkdir()
        self.dll = self.fixture / "SingleFilePlugin.dll"
        shutil.copy2(self.fixture_source / self.dll.name, self.dll)
        shutil.copy2(self.fixture_source / "PPDS.Plugins.dll", self.fixture)
        # Deliberately omit Microsoft.Xrm.Sdk and ALL framework references from inputs.
        self.env = os.environ.copy()
        for key in ("TMP", "TEMP", "TMPDIR"):
            self.env[key] = str(self.temp)
        # Do not allow a caller's bundle extraction settings to unpack managed BCL DLLs
        # and accidentally make a broken embedded-reference publish look healthy.
        for key in list(self.env):
            if key.startswith("DOTNET_BUNDLE_"):
                del self.env[key]
        self.env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(self.temp / "bundle")
        self.env["PPDS_CONFIG_DIR"] = str(self.case / "config")
        self.assertEqual([self.binary], list(self.binary.parent.iterdir()))

    def extract(self, source, *args, succeeds=True):
        result = subprocess.run(
            [str(self.binary), "plugins", "extract", "--input", str(source),
             "--output-format", "json", "--quiet", *map(str, args)],
            cwd=self.case, env=self.env, capture_output=True, text=True,
            encoding="utf-8", timeout=90, check=False,
        )
        diagnostic = f"exit={result.returncode}\nstdout={result.stdout}\nstderr={result.stderr}"
        self.assertEqual(0 if succeeds else 2, result.returncode, diagnostic)
        # Parsing the whole stream also rejects status text mixed into JSON stdout.
        try:
            envelope = json.loads(result.stdout)
        except json.JSONDecodeError:
            self.fail(diagnostic)
        self.assertIs(envelope["success"], succeeds, diagnostic)
        # The startup version/platform header belongs on stderr. Any additional
        # diagnostic (especially embedded-reference fallback warnings) fails the smoke.
        diagnostics = [line for line in result.stderr.splitlines() if line.strip()
                       and not line.startswith(("PPDS CLI v", "Platform: "))]
        self.assertEqual([], diagnostics, diagnostic)
        self.assertFalse((self.fixture / "registrations.json").exists())
        return envelope

    def assert_registration(self, envelope, kind):
        assemblies = envelope["data"]["assemblies"]
        self.assertEqual(1, len(assemblies))
        assembly = assemblies[0]
        self.assertEqual("SingleFilePlugin", assembly["name"])
        self.assertEqual(kind, assembly["type"])
        self.assertEqual(["SingleFileFixture.AccountPlugin"], assembly["allTypeNames"])
        self.assertEqual(1, len(assembly["types"]))
        plugin = assembly["types"][0]
        self.assertEqual("SingleFileFixture.AccountPlugin", plugin["typeName"])
        self.assertEqual(1, len(plugin["steps"]))
        step = plugin["steps"][0]
        for key, value in {
            "message": "Update", "entity": "account", "stage": "PreOperation",
            "mode": "Synchronous", "executionOrder": 7,
            "filteringAttributes": "name,telephone1",
        }.items():
            self.assertEqual(value, step[key], key)
        self.assertEqual(1, len(step["images"]))
        image = step["images"][0]
        self.assertEqual("Before", image["name"])
        self.assertEqual("PreImage", image["imageType"])
        self.assertEqual("name,telephone1", image["attributes"])
        return assembly

    def assert_embedded_cache(self):
        cache = self.cache
        self.assertTrue(cache.is_dir(), f"Expected the private embedded-reference cache: {cache}")
        actual = {p.relative_to(cache).as_posix() for p in cache.rglob("*") if p.is_file()}
        self.assertEqual(set(self.expected_references) | {".complete"}, actual)
        for name, expected in self.expected_references.items():
            self.assertTrue((cache / name).read_bytes() == expected,
                            f"Cached bytes differ from published embedded resource: {name}")
        for name in ("mscorlib.dll", "System.Runtime.dll"):
            self.assertGreater((cache / name).stat().st_size, 0)
        self.assertTrue((cache / ".complete").is_file())
        self.assertEqual(0, (cache / ".complete").stat().st_size)
        # References belong only in protected account storage. Neither a legacy
        # temp cache nor managed-DLL bundle extraction may satisfy the smoke test.
        self.assertEqual([], list(self.case.rglob("mscorlib.dll")))
        self.assertFalse(list((self.temp / "bundle").rglob("System.Private.CoreLib.dll")))
        return cache

    def package(self, name="fixture.nupkg", broken=False):
        package = self.fixture / name
        with zipfile.ZipFile(package, "w") as archive:
            archive.writestr("SingleFilePlugin.nuspec", """
                <package><metadata><id>SingleFilePlugin</id><version>1.0.0</version>
                <authors>PPDS tests</authors><description>Local smoke fixture</description>
                </metadata></package>""")
            if broken:
                archive.writestr("lib/net462/Broken.dll", b"not a PE assembly")
            else:
                for name in ("SingleFilePlugin.dll", "PPDS.Plugins.dll"):
                    archive.write(self.fixture / name, f"lib/net462/{name}")
        return package

    def test_dll_extraction_cold_and_warm_cache(self):
        cold = self.assert_registration(self.extract(self.dll), "Assembly")
        cache = self.assert_embedded_cache()
        before = {p.name: p.stat().st_mtime_ns for p in cache.iterdir()}
        warm = self.assert_registration(self.extract(self.dll), "Assembly")
        self.assertEqual(cold, warm)
        self.assert_embedded_cache()
        self.assertEqual(before, {p.name: p.stat().st_mtime_ns for p in cache.iterdir()})

    def test_package_extraction(self):
        self.assert_registration(self.extract(self.package()), "Nuget")
        self.assert_embedded_cache()

    def test_missing_dependency_and_reference_dir(self):
        references = self.case / "reference directory"
        references.mkdir()
        (self.fixture / "PPDS.Plugins.dll").rename(references / "PPDS.Plugins.dll")
        failure = self.extract(self.dll, succeeds=False)
        self.assertEqual("Operation.Dependency", failure["error"]["code"])
        self.assertIn("PPDS.Plugins", failure["error"]["message"])
        self.assert_registration(
            self.extract(self.dll, "--reference-dir", references), "Assembly")
        self.assert_embedded_cache()

    def test_invalid_package_reports_error(self):
        failure = self.extract(self.package("broken.nupkg", broken=True), succeeds=False)
        self.assertEqual("Operation.Dependency", failure["error"]["code"])
        self.assertIn("Broken.dll", failure["error"]["message"])
        self.assertNotIn("data", failure)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--fixture", required=True, type=Path,
                        help="Built SingleFilePlugin output directory")
    parser.add_argument("--cache-probe", required=True, type=Path,
                        help="Built ReferenceCacheProbe.dll (read-only OS location query)")
    parser.add_argument("--reference-archive", required=True, type=Path,
                        help="Net462ReferenceAssemblies.zip from this CLI publish's obj directory")
    parser.add_argument("--allow-existing-cache", action="store_true",
                        help="Local warm-cache verification only; CI must require a cold cache")
    options = parser.parse_args()
    SingleFileExtractionTests.binary_source = options.binary.resolve(strict=True)
    SingleFileExtractionTests.fixture_source = options.fixture.resolve(strict=True)
    SingleFileExtractionTests.cache_probe = options.cache_probe.resolve(strict=True)
    SingleFileExtractionTests.reference_archive = options.reference_archive.resolve(strict=True)
    SingleFileExtractionTests.allow_existing_cache = options.allow_existing_cache
    unittest.main(argv=[__file__], verbosity=2)

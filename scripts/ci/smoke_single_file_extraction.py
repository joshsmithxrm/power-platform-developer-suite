#!/usr/bin/env python3
"""Exercise a published single-file CLI, without Dataverse or installed DLL sidecars."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile


class SingleFileExtractionTests(unittest.TestCase):
    binary_source: Path
    fixture_source: Path

    @classmethod
    def setUpClass(cls):
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
        cores = list(self.temp.glob("ppds-*/net462-ref/*/mscorlib.dll"))
        self.assertEqual(1, len(cores), "Expected a fresh embedded-reference cache")
        cache = cores[0].parent
        for name in ("mscorlib.dll", "System.Runtime.dll"):
            self.assertGreater((cache / name).stat().st_size, 0)
        self.assertTrue((cache / ".complete").is_file())
        # Only the embedded-reference cache may supply mscorlib. A framework-dependent
        # app or managed-DLL bundle extraction must not satisfy this smoke test.
        self.assertEqual(cores, list(self.case.rglob("mscorlib.dll")))
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
    options = parser.parse_args()
    SingleFileExtractionTests.binary_source = options.binary.resolve(strict=True)
    SingleFileExtractionTests.fixture_source = options.fixture.resolve(strict=True)
    unittest.main(argv=[__file__], verbosity=2)

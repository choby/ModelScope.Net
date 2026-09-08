import importlib.util
import tempfile
import unittest
import zipfile
from pathlib import Path
from xml.sax.saxutils import escape

spec = importlib.util.spec_from_file_location(
    "verify_nuget_packages", Path(__file__).resolve().parents[1] / "verify_nuget_packages.py"
)
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)
EXPECTED_INTERNAL_DEPENDENCIES = verifier.EXPECTED_INTERNAL_DEPENDENCIES
verify_directory = verifier.verify_directory


class VerifyNugetPackagesTests(unittest.TestCase):
    def test_accepts_complete_aligned_package_set(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_set(root, "0.1.0-preview.1")
            verify_directory(root, "0.1.0-preview.1")

    def test_rejects_misaligned_internal_dependency(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_set(root, "0.1.0-preview.1", wrong_dependency_version=True)
            with self.assertRaisesRegex(ValueError, "versions are not aligned"):
                verify_directory(root, "0.1.0-preview.1")

    def test_rejects_missing_symbol_package(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_set(root, "0.1.0-preview.1")
            (root / "ModelScope.Net.Hub.0.1.0-preview.1.snupkg").unlink()
            with self.assertRaisesRegex(ValueError, "symbol package set mismatch"):
                verify_directory(root, "0.1.0-preview.1")

    @staticmethod
    def _write_set(root: Path, version: str, wrong_dependency_version: bool = False):
        for package_id, dependencies in EXPECTED_INTERNAL_DEPENDENCIES.items():
            dependency_xml = "".join(
                f'<dependency id="{escape(dependency)}" version="'
                f'{"9.9.9" if wrong_dependency_version and package_id == "ModelScope.Net.Hub" else version}" />'
                for dependency in sorted(dependencies)
            )
            nuspec = f'''<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
<id>{package_id}</id><version>{version}</version><authors>test</authors>
<license type="expression">Apache-2.0</license><readme>README.md</readme>
<description>test package</description><repository type="git" url="https://example.invalid/repo" />
<dependencies><group targetFramework="net10.0">{dependency_xml}</group></dependencies>
</metadata></package>'''
            with zipfile.ZipFile(root / f"{package_id}.{version}.nupkg", "w") as package:
                package.writestr(f"{package_id}.nuspec", nuspec)
                package.writestr("README.md", "test")
                package.writestr(f"lib/net10.0/{package_id}.dll", b"test")
            with zipfile.ZipFile(root / f"{package_id}.{version}.snupkg", "w") as symbols:
                symbols.writestr(f"lib/net10.0/{package_id}.pdb", b"test")


if __name__ == "__main__":
    unittest.main()

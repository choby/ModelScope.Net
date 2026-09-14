import importlib.util
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from xml.sax.saxutils import escape

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location(
    "generate_sbom", Path(__file__).resolve().parents[1] / "generate_sbom.py"
)
sbom = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sbom)


class SbomTests(unittest.TestCase):
    version = "0.1.0-preview.1"

    def test_generates_bounded_release_inventory_without_local_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            packages = root / "artifacts" / "packages"
            packages.mkdir(parents=True)
            self._write_package_set(packages)
            data = self._package_data(root)

            result = sbom.generate_bom(root, packages, self.version, data, "2026-09-09T00:00:00Z")
            sbom.validate_bom(result)
            serialized = json.dumps(result)
            components = {item["name"]: item for item in result["components"]}

            self.assertEqual("1.7", result["specVersion"])
            self.assertEqual("2026-09-09T00:00:00Z", result["metadata"]["timestamp"])
            self.assertEqual(8, len([name for name in components if name.startswith("ModelScope.Net.")]))
            self.assertIn("Example.Runtime", components)
            self.assertIn("Example.Transitive", components)
            self.assertNotIn("Grpc.Tools", components)
            self.assertNotIn("xunit", components)
            self.assertNotIn(str(root), serialized)
            self.assertRegex(components["ModelScope.Net.Runtime.Onnx"]["hashes"][0]["content"], "^[0-9a-f]{64}$")

    def test_rejects_project_path_outside_root_and_conflicting_versions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            data = self._package_data(root)
            data["projects"][0]["path"] = "/private/outside/ModelScope.Net.Abstractions.csproj"
            with self.assertRaisesRegex(ValueError, "outside-root"):
                sbom.collect_project_packages(root, data)

            data = self._package_data(root)
            runtime = next(item for item in data["projects"] if item["path"].endswith("ModelScope.Net.Runtime.csproj"))
            runtime["frameworks"][0]["transitivePackages"] = [
                {"id": "Example.Transitive", "resolvedVersion": "9.9.9"}
            ]
            with self.assertRaisesRegex(ValueError, "conflicting"):
                sbom.collect_project_packages(root, data)

    def test_validator_rejects_unknown_dependency_and_sensitive_path(self):
        base = {
            "$schema": sbom.SCHEMA,
            "bomFormat": "CycloneDX",
            "specVersion": "1.7",
            "metadata": {"component": {"bom-ref": "root"}},
            "components": [{"bom-ref": "component"}],
            "dependencies": [
                {"ref": "root", "dependsOn": ["missing"]},
                {"ref": "component", "dependsOn": []},
            ],
        }
        with self.assertRaisesRegex(ValueError, "unknown-dependency"):
            sbom.validate_bom(base)
        base["dependencies"][0]["dependsOn"] = ["component"]
        base["components"][0]["name"] = str(Path.home())
        with self.assertRaisesRegex(ValueError, "sensitive"):
            sbom.validate_bom(base)

    def _write_package_set(self, packages: Path):
        for package_id, internal in sbom.EXPECTED_INTERNAL_DEPENDENCIES.items():
            external = '<dependency id="Example.Runtime" version="2.0.0" />' if package_id == "ModelScope.Net.Runtime.Onnx" else ""
            dependencies = "".join(
                f'<dependency id="{escape(name)}" version="{self.version}" />' for name in sorted(internal)
            ) + external
            nuspec = f'''<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
<id>{package_id}</id><version>{self.version}</version><authors>test</authors>
<license type="expression">Apache-2.0</license><readme>README.md</readme>
<description>test package</description><repository type="git" url="https://example.invalid/repo" />
<dependencies><group targetFramework="net10.0">{dependencies}</group></dependencies>
</metadata></package>'''
            with zipfile.ZipFile(packages / f"{package_id}.{self.version}.nupkg", "w") as package:
                package.writestr(f"{package_id}.nuspec", nuspec)
                package.writestr("README.md", "test")
                package.writestr(f"lib/net10.0/{package_id}.dll", b"test")
            with zipfile.ZipFile(packages / f"{package_id}.{self.version}.snupkg", "w") as symbols:
                symbols.writestr(f"lib/net10.0/{package_id}.pdb", b"test")

    @staticmethod
    def _package_data(root: Path):
        projects = []
        for package_id in sbom.EXPECTED_INTERNAL_DEPENDENCIES:
            transitive = []
            if package_id == "ModelScope.Net.Runtime.Onnx":
                transitive = [{"id": "Example.Transitive", "resolvedVersion": "1.0.0"}]
            projects.append({
                "path": str(root / "src" / package_id / f"{package_id}.csproj"),
                "frameworks": [{"framework": "net10.0", "transitivePackages": transitive}],
            })
        projects.append({
            "path": str(root / "tests" / "Tests.csproj"),
            "frameworks": [{"framework": "net10.0", "topLevelPackages": [
                {"id": "xunit", "resolvedVersion": "2.9.3"}
            ]}],
        })
        return {"version": 1, "projects": projects}


if __name__ == "__main__":
    unittest.main()

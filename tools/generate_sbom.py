#!/usr/bin/env python3
"""Generate a CycloneDX SBOM for the local ModelScope.Net NuGet package set."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import urllib.parse
import uuid
import zipfile
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from xml.etree import ElementTree

from verify_nuget_packages import EXPECTED_INTERNAL_DEPENDENCIES, verify_directory


SCHEMA = "https://cyclonedx.org/schema/bom-1.7.schema.json"


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def package_ref(package_id: str, version: str) -> str:
    name = urllib.parse.quote(package_id, safe=".-_")
    resolved = urllib.parse.quote(version, safe=".-_")
    return f"pkg:nuget/{name}@{resolved}"


def safe_path(root: Path, value: str) -> Path:
    parsed = PurePosixPath(value)
    if not value or parsed.is_absolute() or ".." in parsed.parts or "\\" in value:
        raise ValueError("invalid-relative-path")
    path = (root / value).resolve()
    try:
        path.relative_to(root.resolve())
    except ValueError as error:
        raise ValueError("path-outside-root") from error
    return path


def read_nuspec(package_path: Path, package_id: str) -> tuple[set[str], set[tuple[str, str]]]:
    with zipfile.ZipFile(package_path) as package:
        root = ElementTree.fromstring(package.read(f"{package_id}.nuspec"))
    internal: set[str] = set()
    external: set[tuple[str, str]] = set()
    for dependency in root.findall(".//{*}dependency"):
        dependency_id = dependency.get("id", "").strip()
        version = dependency.get("version", "").strip().strip("[]")
        if not dependency_id or not version:
            raise ValueError("invalid-nuspec-dependency")
        if dependency_id in EXPECTED_INTERNAL_DEPENDENCIES:
            internal.add(dependency_id)
        else:
            external.add((dependency_id, version))
    return internal, external


def collect_project_packages(root: Path, data: dict) -> dict[str, set[tuple[str, str]]]:
    expected = set(EXPECTED_INTERNAL_DEPENDENCIES)
    packages: dict[str, set[tuple[str, str]]] = {name: set() for name in expected}
    versions: dict[str, str] = {}
    seen_projects: set[str] = set()
    for project in data.get("projects", []):
        project_path = Path(project.get("path", "")).resolve()
        try:
            relative = project_path.relative_to(root.resolve())
        except ValueError as error:
            raise ValueError("project-path-outside-root") from error
        if not relative.parts or relative.parts[0] != "src":
            continue
        project_id = project_path.stem
        if project_id not in expected:
            continue
        seen_projects.add(project_id)
        for framework in project.get("frameworks", []):
            if framework.get("framework") != "net10.0":
                continue
            for item in framework.get("transitivePackages", []):
                pair = _package_pair(item)
                _record_version(versions, pair)
                packages[project_id].add(pair)
    if seen_projects != expected:
        raise ValueError("publishable-project-set-mismatch")
    return packages


def _package_pair(item: dict) -> tuple[str, str]:
    package_id = item.get("id", "").strip()
    version = item.get("resolvedVersion", "").strip()
    if not package_id or not version:
        raise ValueError("invalid-resolved-package")
    return package_id, version


def _record_version(versions: dict[str, str], pair: tuple[str, str]) -> None:
    package_id, version = pair
    key = package_id.casefold()
    previous = versions.setdefault(key, version)
    if previous != version:
        raise ValueError("conflicting-resolved-package-versions")


def generate_bom(
    root: Path,
    package_directory: Path,
    version: str,
    package_data: dict,
    timestamp: str | None = None,
) -> dict:
    verify_directory(package_directory, version)
    project_packages = collect_project_packages(root, package_data)
    internal_components: list[dict] = []
    external_pairs: set[tuple[str, str]] = set()
    external_versions: dict[str, str] = {}
    dependency_map: dict[str, set[str]] = {}

    for package_id in sorted(EXPECTED_INTERNAL_DEPENDENCIES):
        filename = f"{package_id}.{version}.nupkg"
        package_path = package_directory / filename
        internal, direct_external = read_nuspec(package_path, package_id)
        closure = set(project_packages[package_id]) | direct_external
        for pair in closure:
            _record_version(external_versions, pair)
        external_pairs.update(closure)
        bom_ref = package_ref(package_id, version)
        dependency_map[bom_ref] = {
            *(package_ref(name, version) for name in internal),
            *(package_ref(name, resolved) for name, resolved in closure),
        }
        internal_components.append({
            "type": "library",
            "bom-ref": bom_ref,
            "group": "ModelScope.Net",
            "name": package_id,
            "version": version,
            "scope": "required",
            "hashes": [{"alg": "SHA-256", "content": sha256(package_path)}],
            "licenses": [{"license": {"id": "Apache-2.0"}}],
            "purl": bom_ref,
            "properties": [{"name": "modelscope:artifact:file", "value": filename}],
        })

    external_components = [{
        "type": "library",
        "bom-ref": package_ref(package_id, resolved),
        "name": package_id,
        "version": resolved,
        "scope": "required",
        "purl": package_ref(package_id, resolved),
    } for package_id, resolved in sorted(external_pairs, key=lambda item: (item[0].casefold(), item[1]))]

    root_ref = f"urn:modelscope-net:distribution:{version}"
    dependencies = [{"ref": root_ref, "dependsOn": sorted(component["bom-ref"] for component in internal_components)}]
    dependencies.extend({"ref": ref, "dependsOn": sorted(values)} for ref, values in sorted(dependency_map.items()))
    dependencies.extend({"ref": component["bom-ref"], "dependsOn": []} for component in external_components)
    components = internal_components + external_components
    identity = json.dumps({"components": components, "dependencies": dependencies}, sort_keys=True, separators=(",", ":"))
    serial = uuid.uuid5(uuid.NAMESPACE_URL, identity)
    generated_at = timestamp or datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    bom = {
        "$schema": SCHEMA,
        "bomFormat": "CycloneDX",
        "specVersion": "1.7",
        "serialNumber": f"urn:uuid:{serial}",
        "version": 1,
        "metadata": {
            "timestamp": generated_at,
            "component": {
                "type": "application",
                "bom-ref": root_ref,
                "name": "ModelScope.Net local NuGet distribution",
                "version": version,
                "properties": [
                    {"name": "modelscope:evidence-scope", "value": "local-technical-preview"},
                    {"name": "modelscope:dependency-graph", "value": "flattened-per-package-runtime-closure"},
                ],
            },
        },
        "components": components,
        "dependencies": dependencies,
    }
    validate_bom(bom)
    return bom


def validate_bom(bom: dict) -> None:
    if (bom.get("$schema"), bom.get("bomFormat"), bom.get("specVersion")) != (SCHEMA, "CycloneDX", "1.7"):
        raise ValueError("invalid-cyclonedx-identity")
    components = bom.get("components", [])
    refs = [component.get("bom-ref") for component in components]
    if not components or any(not ref for ref in refs) or len(refs) != len(set(refs)):
        raise ValueError("invalid-component-references")
    root_ref = bom.get("metadata", {}).get("component", {}).get("bom-ref")
    allowed = set(refs) | {root_ref}
    dependency_refs = set()
    for dependency in bom.get("dependencies", []):
        ref = dependency.get("ref")
        if ref not in allowed or ref in dependency_refs:
            raise ValueError("invalid-dependency-reference")
        dependency_refs.add(ref)
        if any(item not in allowed for item in dependency.get("dependsOn", [])):
            raise ValueError("unknown-dependency-target")
    if dependency_refs != allowed:
        raise ValueError("incomplete-dependency-inventory")
    serialized = json.dumps(bom, sort_keys=True)
    if str(Path.home()) in serialized or "MODELSCOPE_API_TOKEN" in serialized:
        raise ValueError("local-or-sensitive-data-in-sbom")


def package_list(root: Path) -> dict:
    completed = subprocess.run(
        ["dotnet", "list", "ModelScope.Net.sln", "package", "--include-transitive", "--format", "json"],
        cwd=root,
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
    )
    if completed.returncode:
        raise ValueError("dotnet-package-list-failed")
    return json.loads(completed.stdout)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("package_directory")
    parser.add_argument("version")
    parser.add_argument("output")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    try:
        root = args.root.resolve()
        package_directory = safe_path(root, args.package_directory)
        output = safe_path(root, args.output)
        bom = generate_bom(root, package_directory, args.version, package_list(root))
        output.parent.mkdir(parents=True, exist_ok=True)
        temporary = output.with_suffix(output.suffix + ".tmp")
        temporary.write_text(json.dumps(bom, indent=2, ensure_ascii=False) + "\n")
        temporary.replace(output)
    except (OSError, ValueError, json.JSONDecodeError, subprocess.TimeoutExpired, zipfile.BadZipFile, ElementTree.ParseError) as error:
        print(f"SBOM generation failed: {type(error).__name__}", file=sys.stderr)
        return 1
    print(f"Generated CycloneDX 1.7 SBOM: {output.relative_to(root)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

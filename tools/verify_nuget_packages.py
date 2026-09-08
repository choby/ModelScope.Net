#!/usr/bin/env python3
"""Validate the local ModelScope.Net NuGet package set without publishing it."""

from __future__ import annotations

import argparse
import sys
import zipfile
from pathlib import Path, PurePosixPath
from xml.etree import ElementTree


EXPECTED_INTERNAL_DEPENDENCIES = {
    "ModelScope.Net.Abstractions": set(),
    "ModelScope.Net.Hub": {"ModelScope.Net.Abstractions"},
    "ModelScope.Net.Runtime": {"ModelScope.Net.Abstractions"},
    "ModelScope.Net.Runtime.Gguf": {"ModelScope.Net.Abstractions", "ModelScope.Net.Runtime"},
    "ModelScope.Net.Runtime.Onnx": {"ModelScope.Net.Abstractions", "ModelScope.Net.Runtime"},
    "ModelScope.Net.Runtime.Python": {"ModelScope.Net.Abstractions", "ModelScope.Net.Runtime"},
    "ModelScope.Net.Runtime.Remote": {"ModelScope.Net.Abstractions", "ModelScope.Net.Runtime"},
    "ModelScope.Net.AspNetCore": {
        "ModelScope.Net.Abstractions",
        "ModelScope.Net.Hub",
        "ModelScope.Net.Runtime",
        "ModelScope.Net.Runtime.Gguf",
        "ModelScope.Net.Runtime.Onnx",
        "ModelScope.Net.Runtime.Python",
        "ModelScope.Net.Runtime.Remote",
    },
}


def _text(metadata: ElementTree.Element, name: str) -> str:
    value = metadata.findtext(f"{{*}}{name}")
    if value is None or not value.strip():
        raise ValueError(f"missing package metadata: {name}")
    return value.strip()


def verify_package(path: Path, expected_id: str, version: str) -> None:
    with zipfile.ZipFile(path) as package:
        names = package.namelist()
        for name in names:
            parsed = PurePosixPath(name)
            if parsed.is_absolute() or ".." in parsed.parts or "\\" in name:
                raise ValueError(f"{path.name}: unsafe archive path {name!r}")

        required = {
            f"{expected_id}.nuspec",
            "README.md",
            f"lib/net10.0/{expected_id}.dll",
        }
        missing = required.difference(names)
        if missing:
            raise ValueError(f"{path.name}: missing entries {sorted(missing)}")
        if any(name.endswith(".pdb") for name in names):
            raise ValueError(f"{path.name}: symbols must be in the .snupkg, not the runtime package")

        root = ElementTree.fromstring(package.read(f"{expected_id}.nuspec"))
        metadata = root.find("{*}metadata")
        if metadata is None:
            raise ValueError(f"{path.name}: missing nuspec metadata")
        if _text(metadata, "id") != expected_id:
            raise ValueError(f"{path.name}: package id mismatch")
        if _text(metadata, "version") != version:
            raise ValueError(f"{path.name}: package version mismatch")
        if _text(metadata, "license") != "Apache-2.0":
            raise ValueError(f"{path.name}: license must be Apache-2.0")
        if _text(metadata, "readme") != "README.md":
            raise ValueError(f"{path.name}: package readme mismatch")
        _text(metadata, "description")
        repository = metadata.find("{*}repository")
        if repository is None or repository.get("type") != "git" or not repository.get("url"):
            raise ValueError(f"{path.name}: missing git repository metadata")

        internal = {}
        for dependency in metadata.findall(".//{*}dependency"):
            dependency_id = dependency.get("id", "")
            if dependency_id.startswith("ModelScope.Net."):
                internal[dependency_id] = dependency.get("version")
        expected_dependencies = EXPECTED_INTERNAL_DEPENDENCIES[expected_id]
        if set(internal) != expected_dependencies:
            raise ValueError(
                f"{path.name}: internal dependency mismatch: "
                f"expected {sorted(expected_dependencies)}, got {sorted(internal)}"
            )
        wrong_versions = {name: value for name, value in internal.items() if value != version}
        if wrong_versions:
            raise ValueError(f"{path.name}: internal dependency versions are not aligned: {wrong_versions}")


def verify_directory(package_directory: Path, version: str) -> None:
    if not package_directory.is_dir():
        raise ValueError(f"package directory does not exist: {package_directory}")
    runtime_packages = {
        path.name[: -len(f".{version}.nupkg")]: path
        for path in package_directory.glob(f"*.{version}.nupkg")
        if not path.name.endswith(".snupkg")
    }
    expected_ids = set(EXPECTED_INTERNAL_DEPENDENCIES)
    if set(runtime_packages) != expected_ids:
        raise ValueError(
            f"package set mismatch: expected {sorted(expected_ids)}, got {sorted(runtime_packages)}"
        )
    symbol_ids = {
        path.name[: -len(f".{version}.snupkg")]
        for path in package_directory.glob(f"*.{version}.snupkg")
    }
    if symbol_ids != expected_ids:
        raise ValueError(f"symbol package set mismatch: expected {sorted(expected_ids)}, got {sorted(symbol_ids)}")
    for package_id, path in sorted(runtime_packages.items()):
        verify_package(path, package_id, version)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("package_directory", type=Path)
    parser.add_argument("version")
    args = parser.parse_args()
    try:
        verify_directory(args.package_directory, args.version)
    except (OSError, ValueError, zipfile.BadZipFile, ElementTree.ParseError) as error:
        print(f"NuGet package verification failed: {error}", file=sys.stderr)
        return 1
    print(f"Verified {len(EXPECTED_INTERNAL_DEPENDENCIES)} runtime packages and symbol packages at {args.version}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

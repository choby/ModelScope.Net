"""Read-only provenance for the local real-model drill; not a production lock or SBOM."""
import hashlib
import importlib
import importlib.metadata as metadata
import json
import pathlib
import platform
import sys

from packaging.requirements import Requirement
from packaging.utils import canonicalize_name


def inspect_environment(source_root, dependency_root):
    source_root = pathlib.Path(source_root).resolve()
    dependency_root = pathlib.Path(dependency_root).resolve()
    layer_names = {}
    for distribution in metadata.distributions(path=[str(dependency_root)]):
        name = canonicalize_name(distribution.metadata["Name"])
        layer_names.setdefault(name, []).append(distribution.version)
    duplicates = {name: versions for name, versions in layer_names.items() if len(versions) != 1}
    selected = {}
    for distribution in metadata.distributions():
        name = canonicalize_name(distribution.metadata["Name"])
        selected.setdefault(name, distribution)
    errors = []
    packages = []
    for name, distribution in sorted(selected.items()):
        packages.append({"name": name, "version": distribution.version,
                         "metadataSha256": hashlib.sha256((distribution.read_text("METADATA") or "").encode()).hexdigest()})
        for text in distribution.requires or []:
            requirement = Requirement(text)
            if requirement.marker and not requirement.marker.evaluate({"extra": ""}):
                continue
            dependency = selected.get(canonicalize_name(requirement.name))
            if dependency is None or dependency.version not in requirement.specifier:
                errors.append({"package": name, "dependency": requirement.name,
                               "constraint": str(requirement.specifier),
                               "actual": dependency.version if dependency else None})
    modules = {}
    for module_name, distribution_name in [
        ("torch", "torch"), ("transformers", "transformers"), ("datasets", "datasets"),
        ("huggingface_hub", "huggingface-hub"), ("grpc", "grpcio"), ("attrs", "attrs"),
    ]:
        module = importlib.import_module(module_name)
        actual = str(module.__version__)
        declared = selected[distribution_name].version
        modules[module_name] = {"version": actual, "metadataVersion": declared,
                                "entrySha256": hashlib.sha256(pathlib.Path(module.__file__).read_bytes()).hexdigest()}
        if actual != declared:
            errors.append({"module": module_name, "actual": actual, "metadata": declared})
    modelscope = importlib.import_module("modelscope")
    if pathlib.Path(modelscope.__file__).resolve() != source_root / "modelscope" / "__init__.py":
        errors.append({"error": "ModelScope source root mismatch"})
    source_files = []
    for path in sorted((source_root / "modelscope").rglob("*.py")):
        source_files.append({"path": path.relative_to(source_root).as_posix(),
                             "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    tree_bytes = json.dumps(source_files, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode()
    return {"schemaVersion": 1, "status": "passed" if not errors and not duplicates else "failed",
            "scope": "selected metadata constraints without extras; Python source files only; not full package content lock",
            "python": platform.python_version(), "platform": platform.platform(), "machine": platform.machine(),
            "pythonExecutableSha256": hashlib.sha256(pathlib.Path(sys.executable).resolve().read_bytes()).hexdigest(),
            "modelscopeVersion": modelscope.__version__, "sourcePythonFileCount": len(source_files),
            "sourcePythonTreeSha256": hashlib.sha256(tree_bytes).hexdigest(), "sourcePythonFiles": source_files,
            "modules": modules, "packages": packages, "duplicateLayerMetadata": duplicates, "constraintErrors": errors}


if __name__ == "__main__":
    result = inspect_environment(sys.argv[1], sys.argv[2])
    print(json.dumps(result, separators=(",", ":")))
    raise SystemExit(0 if result["status"] == "passed" else 1)

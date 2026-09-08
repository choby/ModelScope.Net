"""Aggregate pre-existing SD1.5 evidence into a hash-bound local certification report."""
import hashlib
import json
from pathlib import Path
import sys
import uuid
from PIL import Image

from verify_sd15_weights import verify
from validation_environment import inspect_environment


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def rgb(path):
    with Image.open(path) as image:
        if image.format != "PNG" or image.size != (512, 512):
            raise ValueError("unexpected certification image")
        return image.convert("RGB").tobytes()


if __name__ == "__main__":
    if len(sys.argv) != 9:
        raise SystemExit("usage: aggregate NET_ROOT SOURCE_ROOT PY0 DOTNET0 PY1 DOTNET1 PY2 DOTNET2")
    root, source = map(lambda value: Path(value).resolve(), sys.argv[1:3])
    evidence = [Path(value).resolve() for value in sys.argv[3:]]
    output = root / "artifacts" / "image-generation" / "certification-runs" / uuid.uuid4().hex
    output.mkdir(parents=True)
    plan_path = root / "tests/compatibility/image-generation-plan.json"
    plan = json.loads(plan_path.read_text(encoding="utf-8"))
    report = {"schemaVersion": 1, "status": "failed",
              "scope": "local macOS CPU, one fixed SD1.5 revision, three exact-pixel samples; sample 0 also stream/recovery",
              "modelId": plan["modelId"], "revision": plan["revision"], "device": plan["device"]}
    try:
        weights = verify(root / "artifacts/image-generation/sd15-snapshot")
        if not weights["passed"]:
            raise ValueError("weight verification failed")
        environment = inspect_environment(source, root / ".image-model-deps")
        if environment["status"] != "passed":
            raise ValueError("environment verification failed")
        environment_path = output / "environment.json"
        environment_path.write_text(json.dumps(environment, indent=2), encoding="utf-8")
        samples = []
        for index, expected in enumerate(plan["samples"]):
            py_dir, net_dir = evidence[index * 2:index * 2 + 2]
            py_report = json.loads((py_dir / "report.json").read_text(encoding="utf-8"))
            net_report = json.loads((net_dir / "report.json").read_text(encoding="utf-8"))
            actual = net_dir / ("recovered.png" if index == 0 else "dotnet.png")
            reference = py_dir / "python.png"
            reference_rgb, actual_rgb = rgb(reference), rgb(actual)
            passed = (py_report.get("status") == "passed" and py_report.get("usableForParity") is True and
                      net_report.get("status") == "passed" and net_report.get("sampleId", expected["id"]) == expected["id"] and
                      net_report.get("modelId") == plan["modelId"] and net_report.get("revision") == plan["revision"] and
                      reference_rgb == actual_rgb)
            if index == 0:
                passed = passed and net_report.get("streamDataEvents") == 1 and net_report.get("streamTerminalEvents") == 1 and net_report.get("recoveredBytesMatch") is True
            samples.append({"id": expected["id"], "passed": passed, "pythonReportSha256": sha(py_dir / "report.json"),
                            "dotnetReportSha256": sha(net_dir / "report.json"), "rgbSha256": hashlib.sha256(reference_rgb).hexdigest(),
                            "differentChannels": sum(a != b for a, b in zip(reference_rgb, actual_rgb))})
        code_paths = [plan_path, root / "worker/python/server.py", root / "worker/python/image_output.py",
                      root / "src/ModelScope.Net.Runtime.Python/ImageGenerationRequest.cs",
                      root / "src/ModelScope.Net.Runtime.Python/ImageGenerationResult.cs",
                      root / "tools/ModelScope.Net.ImageSmoke/Program.cs", source / "modelscope/pipelines/multi_modal/diffusers_wrapped/stable_diffusion/stable_diffusion_pipeline.py"]
        report.update({"weights": weights, "environmentSha256": sha(environment_path), "samples": samples,
                       "codeSha256": {str(path.relative_to(root) if path.is_relative_to(root) else path.relative_to(source)): sha(path) for path in code_paths},
                       "status": "passed" if all(sample["passed"] for sample in samples) else "failed"})
    except Exception as error:
        report["errorType"] = type(error).__name__
        raise
    finally:
        (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(output)
    raise SystemExit(0 if report["status"] == "passed" else 1)

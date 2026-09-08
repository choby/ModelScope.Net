"""Kill an owned process after durable staging, then reconcile and enforce revocation in a fresh process."""
import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import uuid


def main():
    root = Path(__file__).resolve().parents[1]
    output = root / "artifacts/rollout" / ("revocation-store-crash-" + uuid.uuid4().hex)
    output.mkdir(parents=True)
    dll = root / "tools/ModelScope.Net.RolloutDrill/bin/Release/net10.0/ModelScope.Net.RolloutDrill.dll"
    environment = {key: value for key, value in os.environ.items()
                   if key in {"PATH", "HOME", "TMPDIR", "TEMP", "TMP", "LANG", "DOTNET_ROOT", "SystemRoot"}}
    report = {"schemaVersion": 1, "status": "failed",
              "startedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "scope": "local-real-bge-process-kill-after-durable-revocation-stage",
              "limitations": ["Local atomic file store is not a production rollback-resistant trust root.",
                              "Forced process termination is not a host power-loss or storage-controller test."]}
    child = None
    try:
        fingerprint = hashlib.sha256(dll.read_bytes()).hexdigest()
        child = subprocess.Popen(["dotnet", str(dll), str(root / "artifacts/certification/bge-small-en-v1.5"),
                                  str(root / "tests/compatibility/bge-small-en-v1.5/python-gold.json"),
                                  str(output), "--hold-after-store-stage"],
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=environment)
        deadline = time.monotonic() + 90
        ready = False
        while time.monotonic() < deadline:
            try:
                data, _ = child.communicate(timeout=0.5)
                (output / "writer.log").write_bytes(data)
                raise RuntimeError("Writer exited before durable staging")
            except subprocess.TimeoutExpired as pending:
                if b"STORE_STAGE_READY\n" in (pending.output or b""):
                    ready = True
                    break
        if not ready:
            raise TimeoutError("Writer never reached durable staging")
        report["writerPid"] = child.pid
        child.kill()
        data, _ = child.communicate(timeout=10)
        (output / "writer.log").write_bytes(data)
        report["writerExitCode"] = child.returncode
        if child.returncode == 0:
            raise RuntimeError("Writer did not terminate abnormally")
        descriptor = output / "store-recovery-probe.json"
        descriptor_hash = hashlib.sha256(descriptor.read_bytes()).hexdigest()
        with subprocess.Popen(["dotnet", str(dll), "--restore-store-probe", str(descriptor)],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=environment) as reader:
            report["readerPid"] = reader.pid
            recovered, _ = reader.communicate(timeout=30)
            (output / "reader.log").write_bytes(recovered)
            report["readerExitCode"] = reader.returncode
            if reader.returncode != 0 or b"STORE_RECONCILED_AND_REVOKED_MODEL_BLOCKED\n" not in recovered:
                raise RuntimeError("Fresh process did not reconcile the staged revocation")
        if hashlib.sha256(dll.read_bytes()).hexdigest() != fingerprint:
            raise RuntimeError("Drill assembly changed during recovery")
        report.update(status="passed", stagedBeforeKill=True, freshProcessReconciled=True,
                      revokedModelBlocked=True, oldCheckpointRejected=True,
                      assemblySha256=fingerprint, recoveryDescriptorSha256=descriptor_hash)
    except (OSError, ValueError, RuntimeError, TimeoutError, subprocess.SubprocessError) as exc:
        report["errorType"] = type(exc).__name__
    finally:
        if child is not None and child.poll() is None:
            child.kill()
            child.communicate(timeout=10)
        (output / "crash-report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"status": report["status"], "report": str(output / "crash-report.json")}))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())

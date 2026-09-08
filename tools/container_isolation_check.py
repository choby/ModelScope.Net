"""Repeatable local Docker isolation probe; does not certify Kubernetes or model execution."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import uuid

PROBE = r'''
import errno, hashlib, json, os, pathlib, socket
def denied(path):
    try:
        with open(path, "w") as stream: stream.write("probe")
        return False
    except OSError as exc:
        return exc.errno == errno.EROFS
status = dict(line.split(":", 1) for line in pathlib.Path("/proc/self/status").read_text().splitlines() if ":" in line)
pathlib.Path("/tmp/probe").write_text("ok")
try:
    with socket.create_connection(("1.1.1.1", 443), timeout=2): network_denied = False
except OSError:
    network_denied = True
print(json.dumps({
    "uid": os.getuid(), "gid": os.getgid(),
    "readOnlyRoot": denied("/etc/isolation-probe"),
    "readOnlyModels": denied("/models/isolation-probe"),
    "temporaryDirectoryWritable": pathlib.Path("/tmp/probe").read_text() == "ok",
    "networkDenied": network_denied,
    "interfaces": sorted(p.name for p in pathlib.Path("/sys/class/net").iterdir()),
    "activeNonLoopbackInterfaces": sorted(p.name for p in pathlib.Path("/sys/class/net").iterdir()
        if (p / "flags").exists() and int((p / "flags").read_text(), 16) & 1 and p.name != "lo"),
    "noNewPrivileges": status["NoNewPrivs"].strip(),
    "seccomp": status["Seccomp"].strip(), "effectiveCapabilities": status["CapEff"].strip(),
    "cpuMax": pathlib.Path("/sys/fs/cgroup/cpu.max").read_text().strip(),
    "memoryMax": pathlib.Path("/sys/fs/cgroup/memory.max").read_text().strip(),
    "pidsMax": pathlib.Path("/sys/fs/cgroup/pids.max").read_text().strip(),
    "serverSha256": hashlib.sha256(pathlib.Path("/opt/modelscope-worker/server.py").read_bytes()).hexdigest()
}))
'''

def run(args):
    root = Path(__file__).resolve().parents[1]
    run_id = uuid.uuid4().hex
    output = root / "artifacts" / "container-checks" / run_id
    output.mkdir(parents=True)
    name = "modelscope-isolation-" + run_id
    report = {"schemaVersion": 1, "runId": run_id,
              "generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "scope": "local-linux-container-isolation-only", "status": "failed"}
    def docker(*command):
        return subprocess.run(["docker", *command], capture_output=True, text=True, timeout=60, check=True).stdout
    try:
        info = json.loads(docker("image", "inspect", args.image))[0]
        report["imageId"] = info["Id"]
        report["architecture"] = info["Architecture"]
        report["engineVersion"] = docker("version", "--format", "{{.Server.Version}}").strip()
        with tempfile.TemporaryDirectory(prefix="modelscope-isolation-models-") as models:
            result = docker("run", "--rm", "--name", name, "--network", "none", "--read-only",
                            "--user", "65532:65532", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                            "--cpus", "0.5", "--memory", "256m", "--pids-limit", "64",
                            "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m,mode=1777",
                            "--mount", f"type=bind,src={models},dst=/models,readonly",
                            "--entrypoint", "python", info["Id"], "-c", PROBE)
        probe = json.loads(result)
        report["probe"] = probe
        cpu = list(map(int, probe["cpuMax"].split()))
        checks = {"nonRoot": probe["uid"] == probe["gid"] == 65532,
                  "readOnlyRoot": probe["readOnlyRoot"], "readOnlyModels": probe["readOnlyModels"],
                  "temporaryDirectoryWritable": probe["temporaryDirectoryWritable"],
                  "networkIsolation": probe["networkDenied"] and not probe["activeNonLoopbackInterfaces"],
                  "noNewPrivileges": probe["noNewPrivileges"] == "1",
                  "seccompFilter": probe["seccomp"] == "2",
                  "capabilitiesDropped": int(probe["effectiveCapabilities"], 16) == 0,
                  "cpuQuota": cpu[0] * 2 == cpu[1],
                  "memoryQuota": probe["memoryMax"] == str(256 * 1024 * 1024),
                  "pidQuota": probe["pidsMax"] == "64",
                  "currentServerMatchesImage": probe["serverSha256"] == hashlib.sha256((root / "worker/python/server.py").read_bytes()).hexdigest()}
        report["checks"] = checks
        report["status"] = "passed" if all(checks.values()) else "failed"
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as exc:
        report["errorType"] = type(exc).__name__
    finally:
        # Only the uniquely named probe container owned by this invocation is reclaimed.
        subprocess.run(["docker", "rm", "-f", name], capture_output=True, timeout=15)
        (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"status": report["status"], "report": str(output / "report.json")}))
    return 0 if report["status"] == "passed" else 1

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", default="modelscope-net/python-worker:p4-05-local")
    raise SystemExit(run(parser.parse_args()))

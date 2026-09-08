"""Bounded public ModelScope annotated-tag and multiprocess shared-cache acceptance."""
from concurrent.futures import ThreadPoolExecutor
import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid


def main():
    root = Path(__file__).resolve().parents[1]
    output = root / "artifacts/hub-livechecks" / uuid.uuid4().hex
    output.mkdir(parents=True)
    cache = output / "cache"
    model = "damo/nlp_structbert_sentiment-classification_chinese-base"
    tag = "v1.0.0"
    dll = root / "src/ModelScope.Net.Cli/bin/Release/net10.0/ModelScope.Net.Cli.dll"
    environment = {key: value for key, value in os.environ.items()
                   if key in {"PATH", "HOME", "TMPDIR", "TEMP", "TMP", "LANG", "DOTNET_ROOT", "SystemRoot"}}
    report = {"schemaVersion": 1, "status": "failed", "modelId": model, "tag": tag,
              "startedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "scope": "public-annotated-tag-four-process-shared-cache-config-only"}
    def invoke(name, command, env=environment):
        result = subprocess.run(command, capture_output=True, text=True, env=env, timeout=90)
        (output / (name + ".stdout")).write_text(result.stdout)
        (output / (name + ".stderr")).write_text(result.stderr)
        if result.returncode != 0:
            raise RuntimeError(name + " failed")
        return result.stdout.strip()
    def cli(name, *args):
        return invoke(name, ["dotnet", str(dll), *args])
    try:
        refs_text = invoke("git-refs", ["git", "-c", "credential.helper=", "-c", "http.extraHeader=",
            "-c", "http.version=HTTP/1.1", "ls-remote", "https://modelscope.cn/" + model + ".git", "refs/tags/" + tag + "*"],
            {**environment, "GIT_TERMINAL_PROMPT": "0"})
        refs = {line.split()[1]: line.split()[0] for line in refs_text.splitlines()}
        tag_object = refs["refs/tags/" + tag]
        commit = refs["refs/tags/" + tag + "^{}"]
        resolved = cli("resolve", "resolve", model, "--revision", tag, "--endpoint", "https://modelscope.cn")
        if resolved != commit or commit == tag_object:
            raise ValueError("Annotated tag did not resolve to independent Git peeled result")
        def download(index):
            return cli("download-" + str(index), "download", model, "--revision", tag,
                       "--allow", "config.json", "--cache", str(cache), "--endpoint", "https://modelscope.cn")
        with ThreadPoolExecutor(max_workers=4) as pool:
            paths = list(pool.map(download, range(4)))
        snapshot = Path(paths[0])
        if len(set(paths)) != 1 or snapshot.name != commit or not snapshot.is_relative_to(cache):
            raise ValueError("Concurrent downloads did not converge to the Commit snapshot")
        manifest = json.loads((snapshot / ".modelscope-net-manifest.json").read_text())
        entries = manifest["files"]
        expected_model = {"owner": "damo", "name": "nlp_structbert_sentiment-classification_chinese-base"}
        if manifest["modelId"] != expected_model or manifest["resolvedRevision"] != commit or manifest["requestedRevision"] != tag:
            raise ValueError("Wrong snapshot identity")
        if len(entries) != 1 or entries[0]["path"] != "config.json":
            raise ValueError("Unexpected download scope")
        content = (snapshot / "config.json").read_bytes()
        digest = hashlib.sha256(content).hexdigest()
        if len(content) != entries[0]["size"] or digest != entries[0]["sha256"]:
            raise ValueError("Snapshot integrity check failed")
        for revision in (tag, commit):
            offline = cli("offline-" + revision, "download", model, "--revision", revision,
                          "--cache", str(cache), "--local-files-only", "--endpoint", "https://127.0.0.1:1")
            if offline != str(snapshot):
                raise ValueError("Offline lookup did not recover the same snapshot")
        report.update(status="passed", tagObject=tag_object, commit=commit, concurrentProcesses=4,
                      allProcessesSucceeded=True, sharedSnapshot=True, offlineTagAndCommitPassed=True,
                      downloadedFiles=1, downloadedSize=len(content), configSha256=digest,
                      cliSha256=hashlib.sha256(dll.read_bytes()).hexdigest())
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        report["errorType"] = type(error).__name__
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"status": report["status"], "report": str(output / "report.json")}))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())

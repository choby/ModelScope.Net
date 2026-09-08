"""Fixed-revision recertification runner. Standard library only; inference uses .NET."""
import argparse, hashlib, json, math, os, platform, re, signal, subprocess, uuid
from pathlib import Path, PurePosixPath
from datetime import datetime, timezone

PROJECTS = {"embedding":"ModelScope.Net.Certification","classification":"ModelScope.Net.ClassificationCertification",
            "vision":"ModelScope.Net.VisionCertification","gguf":"ModelScope.Net.GgufRuntimeCertification"}

def sha(path):
    with path.open("rb") as f: return hashlib.file_digest(f,"sha256").hexdigest()

def safe(root, rel):
    if not rel or rel.startswith("/") or "\\" in rel or ":" in rel or ".." in PurePosixPath(rel).parts:
        raise ValueError("invalid-relative-path")
    return root / rel  # Final snapshot symlinks to the Hub content cache are supported.

def save(path, obj):
    tmp=path.with_suffix(".tmp")
    tmp.write_text(json.dumps(obj,indent=2,allow_nan=False)+"\n")
    os.replace(tmp,path)

def verify(root, model):
    if model["adapter"] not in PROJECTS or not re.fullmatch("[0-9a-f]{40}",model["revision"]):
        raise ValueError("invalid-model-pin")
    if not model["gates"]: raise ValueError("missing-report-gates")
    first=model["snapshots"][0]
    if (first["modelId"],first["revision"]) != (model["modelId"],model["revision"]):
        raise ValueError("primary-identity-mismatch")
    for snap in model["snapshots"]:
        folder=safe(root,snap["directory"])
        meta=json.loads((folder/".modelscope-net-manifest.json").read_text())
        identity=meta["modelId"]
        if (identity["owner"]+"/"+identity["name"],meta["resolvedRevision"]) != (snap["modelId"],snap["revision"]):
            raise ValueError("snapshot-identity-mismatch")
        if not snap["files"]: raise ValueError("missing-pins")
        for item in snap["files"]:
            path=safe(folder,item["path"])
            if path.stat().st_size!=item["size"] or sha(path)!=item["sha256"]: raise ValueError("artifact-integrity")
    for asset in model["assets"].values():
        if sha(safe(root,asset["path"]))!=asset["sha256"]: raise ValueError("gold-or-runtime-integrity")

def check_report(report,model):
    if (report.get("modelId"),report.get("revision"),report.get("status")) != (model["modelId"],model["revision"],"passed"):
        raise ValueError("report-identity-or-status")
    for path,op,expected in model["gates"]:
        value=report
        for key in path.split("."): value=value[key]
        if op=="equal":
            passed=value==expected and isinstance(value,bool)==isinstance(expected,bool)
        else:
            if type(value) not in (int,float) or not math.isfinite(value): raise ValueError("invalid-numerical-result")
            passed=value<=expected if op=="max" else value>=expected if op=="min" else False
        if not passed: raise ValueError("report-gate-failed")

def run(args,root,log,timeout=600):
    allowed={"PATH","HOME","USERPROFILE","DOTNET_ROOT","TMP","TEMP","TMPDIR","LANG","SYSTEMROOT","WINDIR","NUGET_PACKAGES"}
    env={k:v for k,v in os.environ.items() if k.upper() in allowed}
    env.update(HF_HUB_OFFLINE="1",TRANSFORMERS_OFFLINE="1",DOTNET_CLI_TELEMETRY_OPTOUT="1")
    with log.open("wb") as out:
        p=subprocess.Popen(args,cwd=root,env=env,stdin=subprocess.DEVNULL,stdout=out,stderr=subprocess.STDOUT,start_new_session=os.name!="nt")
        try: return p.wait(timeout)
        except (subprocess.TimeoutExpired,KeyboardInterrupt):
            if os.name=="nt": subprocess.run(["taskkill","/PID",str(p.pid),"/T","/F"],capture_output=True)
            else:
                try: os.killpg(p.pid,signal.SIGKILL)
                except ProcessLookupError: pass
            p.wait()
            raise ValueError("child-timeout-or-interrupted")

def command(root,m,out):
    project=PROJECTS[m["adapter"]]
    cmd=["dotnet",str(root/"tools"/project/"bin/Release/net10.0"/(project+".dll"))]
    asset=lambda key:str(safe(root,m["assets"][key]["path"]))
    snap=lambda index:str(safe(root,m["snapshots"][index]["directory"]))
    if m["adapter"] in ("embedding","classification"): cmd+=["--model",snap(0),"--gold",asset("gold")]
    elif m["adapter"]=="vision": cmd+=["--onnx-model",snap(0),"--source-model",snap(1),"--inputs",asset("inputs"),"--gold",asset("gold")]
    else: cmd+=["--model",snap(0),"--file",m["file"],"--archive",asset("archive"),"--executable",asset("executable")]
    if m["adapter"]!="gguf": cmd+=["--output",str(out/"output.json")]
    return cmd+["--report",str(out/"report.json")]

def fingerprint(root,m,plan):
    files={plan.relative_to(root).as_posix():sha(plan)}
    for name in ("global.json","Directory.Build.props","ModelScope.Net.sln"): files[name]=sha(root/name)
    for folder in ("src","tools"):
        for p in (root/folder).rglob("*"):
            if p.is_file() and not {"bin","obj","__pycache__"}&set(p.parts) and p.suffix in (".cs",".csproj",".py"):
                files[p.relative_to(root).as_posix()]=sha(p)
    binary=root/"tools"/PROJECTS[m["adapter"]]/"bin/Release/net10.0"
    if not (binary/(PROJECTS[m["adapter"]]+".dll")).is_file(): raise ValueError("build-missing")
    for p in binary.rglob("*"):
        if p.is_file() and p.suffix in (".dll",".json",".dylib",".so"): files[p.relative_to(root).as_posix()]=sha(p)
    return files

def execute(root,m,out,plan):
    out.mkdir() # A stale passing report can never be reused.
    result={"key":m["key"],"modelId":m["modelId"],"revision":m["revision"],"status":"failed"}
    try:
        verify(root,m)
        before=fingerprint(root,m,plan)
        result["exitCode"]=run(command(root,m,out),root,out/"process.log")
        if result["exitCode"]: raise ValueError("certifier-exit-failed")
        check_report(json.loads((out/"report.json").read_text()),m)
        verify(root,m)
        if fingerprint(root,m,plan)!=before: raise ValueError("build-changed-during-run")
        result.update(status="passed",reportSha256=sha(out/"report.json"),buildSha256=before)
    except (OSError,ValueError,KeyError,TypeError) as error:
        result["failureType"]=type(error).__name__ # Do not serialize private paths or raw child messages.
    save(out/"result.json",result)
    return result

def main():
    p=argparse.ArgumentParser()
    p.add_argument("--root",type=Path,default=Path(__file__).resolve().parents[1])
    p.add_argument("--plan",default="tests/compatibility/certification-plan.json")
    p.add_argument("--output",default="artifacts/certification-runs")
    p.add_argument("--model",action="append")
    p.add_argument("--no-build",action="store_true")
    args=p.parse_args(); root=args.root.resolve(); planpath=safe(root,args.plan)
    plan=json.loads(planpath.read_text()); models=plan["models"]
    if plan.get("schemaVersion")!=1 or not models: raise ValueError("invalid-plan")
    keys=[m["key"] for m in models]
    if len(set(keys))!=len(keys) or any(not re.fullmatch("[a-z0-9][a-z0-9.-]{0,99}",k) for k in keys): raise ValueError("invalid-keys")
    if args.model:
        if set(args.model)-set(keys): raise ValueError("unknown-model")
        models=[m for m in models if m["key"] in args.model]
    out=safe(root,args.output)/uuid.uuid4().hex; out.mkdir(parents=True)
    summary={"schemaVersion":1,"status":"running","startedAt":datetime.now(timezone.utc).isoformat(),
             "planSha256":sha(planpath),"platform":platform.platform(),"models":[],"scope":"engineering-recertification-not-release-approval"}
    save(out/"summary.json",summary)
    try:
        if not args.no_build and run(["dotnet","build","ModelScope.Net.sln","-c","Release"],root,out/"build.log"):
            raise ValueError("build-failed")
        for m in models:
            result=execute(root,m,out/m["key"],planpath); summary["models"].append(result)
            save(out/"summary.json",summary); print(m["key"]+": "+result["status"],flush=True)
        summary["status"]="passed" if all(r["status"]=="passed" for r in summary["models"]) else "failed"
    except (OSError,ValueError,KeyboardInterrupt) as error: summary.update(status="failed",failureType=type(error).__name__)
    summary["completedAt"]=datetime.now(timezone.utc).isoformat(); save(out/"summary.json",summary)
    print(out.relative_to(root).as_posix()+"/summary.json",flush=True)
    return 0 if summary["status"]=="passed" else 2

if __name__=="__main__": raise SystemExit(main())

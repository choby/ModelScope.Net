"""Read-only upstream inventory and local dependency drift detection."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import urllib.parse
import urllib.request
import uuid
from certification_pipeline import safe, save, sha

def get_json(url):
    request=urllib.request.Request(url,headers={"User-Agent":"ModelScope.Net-upstream-monitor/1"})
    with urllib.request.urlopen(request,timeout=25) as response:
        data=response.read(8*1024*1024+1)
        if len(data)>8*1024*1024: raise ValueError("metadata-too-large")
    return json.loads(data)

def inventory(envelope):
    if envelope.get("Success") is False or envelope.get("Code",200)!=200:
        raise ValueError("upstream-api-failed")
    files=envelope["Data"]["Files"]
    if not isinstance(files,list) or not files: raise ValueError("empty-or-invalid-inventory")
    normalized={}
    for item in files:
        if item.get("Type")=="tree": continue
        path=item["Path"]
        if path in normalized: raise ValueError("duplicate-upstream-file")
        revision=item.get("Revision")
        if not isinstance(revision,str) or len(revision)!=40 or any(c not in "0123456789abcdef" for c in revision):
            raise ValueError("unresolved-file-revision")
        normalized[path]={"revision":revision,"sha256":item.get("Sha256"),"size":item["Size"]}
    if not normalized: raise ValueError("no-upstream-files")
    return normalized

def compare(pinned,tracked):
    return {"added":sorted(tracked.keys()-pinned.keys()),"removed":sorted(pinned.keys()-tracked.keys()),
            "modified":sorted(k for k in pinned.keys()&tracked.keys() if pinned[k]!=tracked[k])}

def fingerprint(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(",",":")).encode()).hexdigest()

def check_model(snapshot,fetch=get_json):
    prefix="https://modelscope.cn/api/v1/models/"+"/".join(urllib.parse.quote(s,safe="") for s in snapshot["modelId"].split("/"))
    def files(revision):
        return inventory(fetch(prefix+"/repo/files?"+urllib.parse.urlencode({"Revision":revision,"Recursive":"True"})))
    pinned=files(snapshot["revision"]); tracked=files("master")
    for expected in snapshot["files"]:
        remote=pinned.get(expected["path"])
        if remote is None or remote["sha256"]!=expected["sha256"] or remote["size"]!=expected["size"]:
            raise ValueError("pinned-metadata-integrity-failed")
    changes=compare(pinned,tracked)
    return {"modelId":snapshot["modelId"],"pinnedRevision":snapshot["revision"],"trackedReference":"master",
            "status":"changed" if any(changes.values()) else "unchanged","changes":changes,
            "pinnedInventorySha256":fingerprint(pinned),"trackedInventorySha256":fingerprint(tracked)}

def dependency_changes(root,baseline):
    result=[]
    for path,expected in baseline.items():
        file=safe(root,path)
        actual=sha(file) if file.is_file() else None
        if actual!=expected: result.append({"path":path,"expectedSha256":expected,"actualSha256":actual})
    for pattern in ("src/*/*.csproj","tests/compatibility/*/python-requirements.txt"):
        for file in sorted(root.glob(pattern)):
            relative=file.relative_to(root).as_posix()
            if relative not in baseline:
                result.append({"path":relative,"expectedSha256":None,"actualSha256":sha(file)})
    return result

def main():
    p=argparse.ArgumentParser()
    p.add_argument("--root",type=Path,default=Path(__file__).resolve().parents[1])
    p.add_argument("--offline",action="store_true")
    p.add_argument("--output",default="artifacts/upstream-checks")
    args=p.parse_args(); root=args.root.resolve()
    plan=json.loads((root/"tests/compatibility/certification-plan.json").read_text())
    baseline=json.loads((root/"tests/compatibility/upstream-baseline.json").read_text())
    changes=dependency_changes(root,baseline["dependencyFiles"])
    report={"schemaVersion":1,"startedAt":datetime.now(timezone.utc).isoformat(),"models":[],
            "dependencyChanges":changes,"planSha256":sha(root/"tests/compatibility/certification-plan.json"),
            "baselineSha256":sha(root/"tests/compatibility/upstream-baseline.json")}
    seen=set()
    if not args.offline:
        for model in plan["models"]:
            for snap in model["snapshots"]:
                identity=(snap["modelId"],snap["revision"])
                if identity in seen: continue
                seen.add(identity)
                try: result=check_model(snap)
                except (OSError,ValueError,KeyError,TypeError) as error:
                    result={"modelId":snap["modelId"],"status":"failed","failureType":type(error).__name__}
                report["models"].append(result)
                print(result["modelId"]+": "+result["status"],flush=True)
    failed=any(m["status"]=="failed" for m in report["models"])
    drift=bool(changes) or any(m["status"]=="changed" for m in report["models"])
    report.update(status="failed" if failed else "changed" if drift else "offline-only" if args.offline else "unchanged",
                  requiresRecertification=drift,upstreamChecked=not args.offline,completedAt=datetime.now(timezone.utc).isoformat())
    out=safe(root,args.output)/uuid.uuid4().hex;out.mkdir(parents=True);save(out/"report.json",report)
    print(out.relative_to(root).as_posix()+"/report.json",flush=True)
    return 2 if failed else 3 if drift else 0

if __name__=="__main__":raise SystemExit(main())

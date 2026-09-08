import importlib.util
import json
import math
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location("pipeline",Path(__file__).resolve().parents[1]/"certification_pipeline.py")
p=importlib.util.module_from_spec(spec); spec.loader.exec_module(p)

class PipelineTests(unittest.TestCase):
    def model(self):
        return {"key":"test-model","modelId":"test/model","revision":"a"*40,
                "adapter":"embedding","assets":{},"snapshots":[],
                "gates":[["results.error","max",0.01]]}

    def report(self):
        return {"modelId":"test/model","revision":"a"*40,"status":"passed","results":{"error":0.001}}

    def test_claimed_pass_with_bad_metric_or_identity_is_rejected(self):
        m=self.model()
        p.check_report(self.report(),m)
        for value in (0.1,float("nan"),float("inf"),True):
            r=self.report();r["results"]["error"]=value
            with self.assertRaises(ValueError):p.check_report(r,m)
        r=self.report();r["revision"]="b"*40
        with self.assertRaises(ValueError):p.check_report(r,m)

    def test_snapshot_and_gold_tampering_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); folder=root/"model";folder.mkdir()
            (folder/"weights").write_bytes(b"model")
            (folder/".modelscope-net-manifest.json").write_text(json.dumps({
                "modelId":{"owner":"test","name":"model"},"resolvedRevision":"a"*40}))
            m=self.model(); m["snapshots"]=[{"directory":"model","modelId":"test/model",
                "revision":"a"*40,"files":[{"path":"weights","size":5,"sha256":p.sha(folder/"weights")}]}]
            p.verify(root,m)
            (folder/"weights").write_bytes(b"other")
            with self.assertRaises(ValueError):p.verify(root,m)
            (folder/"weights").write_bytes(b"model")
            (root/"gold.json").write_text("{}")
            m["assets"]={"gold":{"path":"gold.json","sha256":"0"*64}}
            with self.assertRaises(ValueError):p.verify(root,m)

    def test_missing_report_or_nonzero_exit_cannot_pass(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            with patch.object(p,"verify"),patch.object(p,"fingerprint",return_value={"source":"pin"}),patch.object(p,"command",return_value=[]):
                with patch.object(p,"run",return_value=0):
                    self.assertEqual("failed",p.execute(root,self.model(),root/"missing",root/"plan")["status"])
                with patch.object(p,"run",return_value=2):
                    self.assertEqual("failed",p.execute(root,self.model(),root/"badexit",root/"plan")["status"])

    def test_existing_directory_cannot_reuse_passing_report(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);out=root/"previous";out.mkdir()
            (out/"report.json").write_text(json.dumps(self.report()))
            with self.assertRaises(FileExistsError):p.execute(root,self.model(),out,root/"plan")

    def test_escaping_paths_are_rejected(self):
        for path in ("../secret","/secret",r"C:\secret","model/../../secret"):
            with self.assertRaises(ValueError):p.safe(Path("."),path)

    def test_timeout_reaps_child_and_inference_environment_excludes_tokens(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            with patch.dict(os.environ,{"MODELSCOPE_API_TOKEN":"test-sentinel"}):
                self.assertEqual(0,p.run([sys.executable,"-c",
                    "import os; print('MODELSCOPE_API_TOKEN' in os.environ)"],root,root/"env.log"))
            self.assertEqual("False",(root/"env.log").read_text().strip())
            with self.assertRaisesRegex(ValueError,"timeout"):
                p.run([sys.executable,"-c","import time; time.sleep(30)"],root,root/"timeout.log",timeout=0.1)

if __name__=="__main__":unittest.main()

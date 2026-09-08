import importlib.util
import sys
from pathlib import Path
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import upstream_monitor as m

class UpstreamTests(unittest.TestCase):
    def envelope(self,sha="a"*64,revision="b"*40):
        return {"Code":200,"Data":{"Files":[{"Type":"blob","Path":"model.onnx","Sha256":sha,"Size":5,"Revision":revision}]}}

    def snapshot(self):
        return {"modelId":"test/model","revision":"b"*40,"files":[{"path":"model.onnx","sha256":"a"*64,"size":5}]}

    def test_unchanged_and_changed_revision(self):
        result=m.check_model(self.snapshot(),lambda _:self.envelope())
        self.assertEqual("unchanged",result["status"])
        result=m.check_model(self.snapshot(),lambda url:self.envelope(revision="c"*40) if "Revision=master" in url else self.envelope())
        self.assertEqual(["model.onnx"],result["changes"]["modified"])
        self.assertEqual("changed",result["status"])

    def test_bad_pinned_hash_and_failed_query_are_not_unchanged(self):
        with self.assertRaises(ValueError):m.check_model(self.snapshot(),lambda _:self.envelope(sha="d"*64))
        for envelope in ({"Success":False},{"Data":{"Files":[]}},{"Data":{"Files":[{"Path":"x","Size":0,"Revision":"master"}]}}):
            with self.assertRaises((ValueError,KeyError)):m.inventory(envelope)

    def test_added_removed_files_and_dependency_change(self):
        self.assertEqual({"added":["new"],"removed":["old"],"modified":[]},m.compare({"old":{}},{"new":{}}))
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);file=root/"runtime.csproj";file.write_text("v1")
            baseline={"runtime.csproj":m.sha(file)}
            self.assertEqual([],m.dependency_changes(root,baseline))
            file.write_text("v2")
            self.assertEqual(1,len(m.dependency_changes(root,baseline)))
            file.unlink()
            self.assertIsNone(m.dependency_changes(root,baseline)[0]["actualSha256"])
            added=root/"src/New/New.csproj";added.parent.mkdir(parents=True);added.write_text("new-package")
            self.assertTrue(any(change["path"]=="src/New/New.csproj" and change["expectedSha256"] is None
                                for change in m.dependency_changes(root,baseline)))

if __name__=="__main__":unittest.main()

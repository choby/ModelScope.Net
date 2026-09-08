import pathlib
import tempfile
import types
import unittest
from unittest.mock import patch

from validation_environment import inspect_environment


class Distribution:
    def __init__(self, name, version="1.0", requires=()):
        self.metadata = {"Name": name}
        self.version = version
        self.requires = requires

    def read_text(self, name):
        return str(self.metadata) + self.version


class EnvironmentTests(unittest.TestCase):
    def inspect(self, *, duplicate=False, mismatch=False, requirement=None, drift=False):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            package = root / "modelscope"
            package.mkdir()
            entry = package / "__init__.py"
            entry.write_text("# fixture\n")
            names = {"torch": "torch", "transformers": "transformers", "datasets": "datasets",
                     "huggingface_hub": "huggingface-hub", "grpc": "grpcio", "attrs": "attrs"}
            distributions = [Distribution(name) for name in names.values()]
            if requirement:
                distributions[0].requires = [requirement]
            layer = distributions + ([Distribution("attrs", "2.0")] if duplicate else [])

            def module(name):
                return types.SimpleNamespace(__file__=str(entry), __version__="2.0" if mismatch and name == "torch" else "1.0")

            with patch("validation_environment.metadata.distributions", side_effect=lambda **kwargs: layer if kwargs else distributions), \
                    patch("validation_environment.importlib.import_module", side_effect=module):
                before = inspect_environment(root, root / "deps")
                if drift:
                    entry.write_text("# modified fixture\n")
                    after = inspect_environment(root, root / "deps")
                    self.assertNotEqual(before["sourcePythonTreeSha256"], after["sourcePythonTreeSha256"])
                return before

    def test_consistent_environment_and_source_drift(self):
        self.assertEqual("passed", self.inspect(drift=True)["status"])

    def test_duplicate_metadata_rejected(self):
        result = self.inspect(duplicate=True)
        self.assertEqual("failed", result["status"])
        self.assertIn("attrs", result["duplicateLayerMetadata"])

    def test_module_metadata_mismatch_rejected(self):
        self.assertEqual("failed", self.inspect(mismatch=True)["status"])

    def test_unsatisfied_dependency_rejected(self):
        result = self.inspect(requirement="attrs>=2")
        self.assertEqual("failed", result["status"])
        self.assertEqual("attrs", result["constraintErrors"][0]["dependency"])

    def test_missing_dependency_rejected(self):
        self.assertEqual("failed", self.inspect(requirement="missing-fixture-package>=1")["status"])

    def test_inactive_extra_is_not_required(self):
        self.assertEqual("passed", self.inspect(requirement='missing-fixture-package; extra == "optional"')["status"])


if __name__ == "__main__":
    unittest.main()

import json
import tempfile
import unittest
from pathlib import Path


CONFIG = Path(__file__).resolve().parents[3] / ".releaserc.json"


class ReleaseConfigTests(unittest.TestCase):
    def test_beta_version_floor_guard_runs_in_verify_release(self):
        config = json.loads(CONFIG.read_text())
        plugins = [plugin for plugin, *_ in config["plugins"]]
        self.assertIn("./scripts/release/verify-beta-version-floor.mjs", plugins)
        self.assertLess(
            plugins.index("./scripts/release/verify-beta-version-floor.mjs"),
            plugins.index("@semantic-release/exec"),
        )

    def test_github_plugin_disables_success_comments(self):
        config = json.loads(CONFIG.read_text())
        github_options = next(
            entry[1]
            for entry in config["plugins"]
            if entry[0] == "@semantic-release/github"
        )

        self.assertIs(False, github_options["successCommentCondition"])

    def test_github_assets_use_globs_that_resolve_built_release_files(self):
        config = json.loads(CONFIG.read_text())
        github_options = next(entry[1] for entry in config["plugins"] if entry[0] == "@semantic-release/github")
        with tempfile.TemporaryDirectory() as directory:
            artifacts = Path(directory) / "artifacts"
            artifacts.mkdir()
            for suffix in ("zip", "sha256", "md5"):
                (artifacts / f"m3u-editor-for-emby-1.6.1-beta.2.{suffix}").touch()
            for asset in github_options["assets"][1:]:
                pattern = asset["path"]
                self.assertNotIn("${nextRelease.version}", pattern)
                matches = list((Path(directory) / pattern).parent.glob(Path(pattern).name))
                self.assertEqual(1, len(matches), pattern)


if __name__ == "__main__":
    unittest.main()

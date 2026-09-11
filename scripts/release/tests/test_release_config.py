import json
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


if __name__ == "__main__":
    unittest.main()

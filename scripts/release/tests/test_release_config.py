import json
import unittest
from pathlib import Path


CONFIG = Path(__file__).resolve().parents[3] / ".releaserc.json"


class ReleaseConfigTests(unittest.TestCase):
    def test_github_plugin_disables_success_comments(self):
        config = json.loads(CONFIG.read_text())
        github_options = next(
            options
            for plugin, options in config["plugins"]
            if plugin == "@semantic-release/github"
        )

        self.assertIs(False, github_options["successCommentCondition"])


if __name__ == "__main__":
    unittest.main()

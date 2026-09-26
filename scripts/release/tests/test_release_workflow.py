import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
CI = (ROOT / ".github/workflows/ci.yml").read_text()
RELEASE = (ROOT / ".github/workflows/release.yml").read_text()


class ReleaseWorkflowTests(unittest.TestCase):
    def test_release_is_same_run_job_after_build_and_codeql(self):
        self.assertIn("release:", CI)
        self.assertIn("needs: [build-and-test, codeql]", CI)
        self.assertIn("github.event_name == 'push'", CI)
        self.assertIn("github.ref == 'refs/heads/main'", CI)
        self.assertIn("github.ref == 'refs/heads/develop'", CI)
        self.assertIn("contents: write", CI)
        self.assertNotIn("workflow_run:", CI)
        self.assertNotIn("workflow_run:", RELEASE)

    def test_release_fails_closed_on_head_race(self):
        self.assertIn('git rev-parse "origin/${GITHUB_REF_NAME}"', CI)
        self.assertIn('= "${GITHUB_SHA}"', CI)
        self.assertIn("cancel-in-progress: false", CI)

    def test_release_uses_locked_install_and_fixed_compatible_node(self):
        self.assertIn("node-version: '22.23.2'", CI)
        self.assertIn("npm ci", CI)
        self.assertIn("npm audit --audit-level=high", CI)
        self.assertNotIn("npm ci || npm install", CI)
        package = (ROOT / "package.json").read_text()
        self.assertIn('"node": ">=22.14.0 <23"', package)

    def test_release_is_not_dispatchable_or_pr_triggered(self):
        self.assertNotIn("workflow_dispatch", CI)
        self.assertNotIn("pull_request", CI.split("  release:", 1)[1])


if __name__ == "__main__":
    unittest.main()

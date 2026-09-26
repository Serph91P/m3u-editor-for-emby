import os
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
CI = (ROOT / ".github/workflows/ci.yml").read_text()
GUARD = ROOT / "scripts/release/verify-release-context.sh"


class ReleaseWorkflowTests(unittest.TestCase):
    def test_release_is_same_run_job_after_build_and_codeql(self):
        self.assertIn("release:", CI)
        self.assertIn("needs: [build-and-test, codeql]", CI)
        self.assertIn("github.event_name == 'push'", CI)
        self.assertIn("github.ref == 'refs/heads/main'", CI)
        self.assertIn("github.ref == 'refs/heads/develop'", CI)
        self.assertIn("contents: write", CI)
        self.assertNotIn("workflow_run:", CI)
        self.assertNotIn("workflow_run:", CI)
        self.assertIn("uses: actions/setup-dotnet@v5", CI[CI.index("  release:"):])

    def test_release_fails_closed_on_head_race(self):
        release = CI[CI.index("  release:"):]
        self.assertIn("verify-release-context.sh", release)
        self.assertLess(release.index("verify-release-context.sh"), release.index("npx semantic-release"))
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

    def test_context_guard_accepts_exact_origin_push(self):
        self.assertEqual(self._run_guard({"GITHUB_EVENT_NAME": "push", "GITHUB_REPOSITORY": "Serph91P/m3u-editor-for-emby", "GITHUB_REF_NAME": "main", "GITHUB_REF": "refs/heads/main", "GITHUB_SHA": "abc", "RELEASE_REMOTE_SHA": "abc"}).returncode, 0)

    def test_context_guard_rejects_foreign_repo_pull_request_branch_and_stale_sha(self):
        base = {"GITHUB_EVENT_NAME": "push", "GITHUB_REPOSITORY": "Serph91P/m3u-editor-for-emby", "GITHUB_REF_NAME": "main", "GITHUB_REF": "refs/heads/main", "GITHUB_SHA": "abc", "RELEASE_REMOTE_SHA": "abc"}
        for key, value in (("GITHUB_EVENT_NAME", "pull_request"), ("GITHUB_REPOSITORY", "other/repo"), ("GITHUB_REF_NAME", "feature/x"), ("RELEASE_REMOTE_SHA", "def")):
            env = {**base, key: value}
            self.assertNotEqual(self._run_guard(env).returncode, 0, key)

    def _run_guard(self, values):
        env = os.environ.copy()
        env.update(values)
        return subprocess.run([str(GUARD)], env=env, text=True, capture_output=True)


if __name__ == "__main__":
    unittest.main()

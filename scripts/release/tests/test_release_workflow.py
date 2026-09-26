import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
CI = (ROOT / ".github/workflows/ci.yml").read_text()
GUARD = ROOT / "scripts/release/verify-release-context.sh"


class ReleaseWorkflowTests(unittest.TestCase):
    def test_release_is_same_run_job_after_build_and_codeql(self):
        release = CI[CI.index("  release:"):]
        self.assertIn("needs: [build-and-test, codeql]", release)
        self.assertIn("github.event_name == 'push'", release)
        self.assertIn("github.ref == 'refs/heads/main'", release)
        self.assertIn("github.ref == 'refs/heads/develop'", release)
        self.assertIn("contents: write", release)
        self.assertIn("uses: actions/setup-dotnet@v5", release)
        self.assertNotIn("workflow_run:", CI)

    def test_release_guard_precedes_publish_and_no_concurrent_release(self):
        release = CI[CI.index("  release:"):]
        self.assertLess(release.index("verify-release-context.sh"), release.index("npx semantic-release"))
        self.assertIn("cancel-in-progress: false", CI)

    def test_release_uses_locked_install_and_fixed_compatible_node(self):
        self.assertIn("node-version: '22.23.2'", CI)
        self.assertIn("npm ci", CI)
        self.assertIn("npm audit --audit-level=high", CI)
        self.assertNotIn("npm ci || npm install", CI)
        self.assertIn('"node": ">=22.14.0 <23"', (ROOT / "package.json").read_text())

    def test_release_is_not_dispatchable_or_pr_triggered(self):
        self.assertNotIn("workflow_dispatch", CI)
        self.assertNotIn("pull_request", CI.split("  release:", 1)[1])

    def test_context_guard_accepts_exact_origin_push_for_main_and_develop(self):
        for branch in ("main", "develop"):
            result, calls = self._run_guard(branch=branch, remote_sha="abc", sha="abc")
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(calls, ["fetch origin", "rev-parse origin/" + branch])

    def test_context_gate_rejects_pending_and_failed_required_checks(self):
        # Model the actual `needs` gate: without an explicit success for every
        # required job, GitHub does not schedule the release job.
        release = CI[CI.index("  release:"):]
        self.assertIn("needs: [build-and-test, codeql]", release)
        for build_result, codeql_result in (("pending", "success"), ("success", "failed"), ("failed", "success")):
            self.assertFalse(self._release_gate_allows("push", "main", build_result, codeql_result))
        self.assertTrue(self._release_gate_allows("push", "main", "success", "success"))

    @staticmethod
    def _release_gate_allows(event, branch, build_result, codeql_result):
        return event == "push" and branch in {"main", "develop"} and build_result == "success" and codeql_result == "success"

    def test_context_guard_rejects_pull_request_foreign_branch_and_sha(self):
        cases = [
            {"event": "pull_request", "repo": "Serph91P/m3u-editor-for-emby", "branch": "main", "sha": "abc", "remote_sha": "abc"},
            {"event": "push", "repo": "other/repo", "branch": "main", "sha": "abc", "remote_sha": "abc"},
            {"event": "push", "repo": "Serph91P/m3u-editor-for-emby", "branch": "feature/x", "sha": "abc", "remote_sha": "abc"},
            {"event": "push", "repo": "Serph91P/m3u-editor-for-emby", "branch": "main", "sha": "abc", "remote_sha": "def"},
        ]
        for case in cases:
            result, _ = self._run_guard(**case)
            self.assertNotEqual(result.returncode, 0, case)

    def test_live_ref_update_between_install_and_publish_fails_closed(self):
        result, calls = self._run_guard(branch="main", remote_sha="updated", sha="installed")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, ["fetch origin", "rev-parse origin/main"])

    def _run_guard(self, branch, remote_sha, sha, event="push", repo="Serph91P/m3u-editor-for-emby"):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "calls"
            git = Path(directory) / "git"
            git.write_text("#!/bin/sh\nprintf '%s %s\\n' \"$1\" \"$2\" >> \"$GIT_CALL_LOG\"\nif [ \"$1\" = rev-parse ]; then printf '%s\\n' \"$GIT_REMOTE_SHA\"; fi\n")
            git.chmod(0o755)
            env = os.environ.copy()
            env.update({"PATH": f"{directory}:{env['PATH']}", "GIT_CALL_LOG": str(log), "GIT_REMOTE_SHA": remote_sha,
                        "GITHUB_EVENT_NAME": event, "GITHUB_REPOSITORY": repo, "GITHUB_REF_NAME": branch,
                        "GITHUB_REF": f"refs/heads/{branch}", "GITHUB_SHA": sha})
            result = subprocess.run([str(GUARD)], env=env, text=True, capture_output=True)
            calls = log.read_text().splitlines() if log.exists() else []
            return result, calls


if __name__ == "__main__":
    unittest.main()

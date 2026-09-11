import json
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
PLUGIN = ROOT / "scripts/release/verify-beta-version-floor.mjs"
RUNNER = """
const { verifyRelease } = await import(process.argv[1]);
const context = JSON.parse(process.argv[2]);
try {
  await verifyRelease({}, context);
  process.stdout.write("accepted");
} catch (error) {
  process.stderr.write(error.message);
  process.exitCode = 1;
}
"""


def context(next_version="1.6.0-beta.1", tags=None, branch=None):
    return {
        "branch": branch
        or {"name": "develop", "type": "prerelease", "prerelease": "beta"},
        "branches": [{"name": "main", "tags": tags if tags is not None else [{"version": "1.5.1"}]}],
        "nextRelease": {"version": next_version},
    }


class VerifyBetaVersionFloorTests(unittest.TestCase):
    def run_guard(self, release_context):
        return subprocess.run(
            ["node", "--input-type=module", "--eval", RUNNER, str(PLUGIN), json.dumps(release_context)],
            cwd=ROOT,
            text=True,
            capture_output=True,
            check=False,
        )

    def test_stale_beta_is_rejected_before_release(self):
        result = self.run_guard(context("1.5.0-beta.7"))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Merge main into develop", result.stderr)

    def test_higher_beta_is_accepted(self):
        result = self.run_guard(context("1.6.0-beta.1"))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("accepted", result.stdout)

    def test_stable_release_is_unaffected(self):
        result = self.run_guard(
            context("1.5.2", branch={"name": "main", "type": "release"})
        )
        self.assertEqual(0, result.returncode, result.stderr)

    def test_malformed_or_missing_stable_metadata_is_rejected(self):
        malformed = self.run_guard(context(tags=[{"version": "not-a-version"}]))
        missing = self.run_guard(context(tags=[]))
        self.assertNotEqual(0, malformed.returncode)
        self.assertIn("malformed", malformed.stderr)
        self.assertNotEqual(0, missing.returncode)
        self.assertIn("unavailable", missing.stderr)


if __name__ == "__main__":
    unittest.main()

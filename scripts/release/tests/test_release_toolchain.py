import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


class ReleaseToolchainTests(unittest.TestCase):
    def test_real_release_packages_preserve_analysis_notes_and_branch_matching(self):
        result = subprocess.run(
            ["node", str(Path(__file__).with_name("release-toolchain-smoke.mjs"))],
            cwd=ROOT, capture_output=True, text=True, timeout=30,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("no publish hooks invoked", result.stdout)


if __name__ == "__main__":
    unittest.main()

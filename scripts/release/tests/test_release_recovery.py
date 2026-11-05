"""Offline integration tests: disposable Git remotes and a synthetic GitHub server."""
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


class ReleaseRecoveryTests(unittest.TestCase):
    def test_release_recovery(self):
        result = subprocess.run(
            ["node", "--test", str(ROOT / "scripts/release/tests/release-recovery.mjs")],
            cwd=ROOT, text=True, capture_output=True, timeout=240,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()

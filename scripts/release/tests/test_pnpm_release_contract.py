import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]


class PnpmReleaseContractTests(unittest.TestCase):
    def test_root_uses_native_scoped_removal_and_locked_manager(self):
        package = json.loads((ROOT / 'package.json').read_text())
        self.assertEqual('pnpm@10.34.9', package.get('packageManager'))
        self.assertNotIn('overrides', package)
        policy = (ROOT / 'pnpm-workspace.yaml').read_text()
        self.assertIn("'semantic-release>@semantic-release/npm': '-'", policy)
        self.assertIn("handlebars: '4.7.10'", policy)
        self.assertTrue((ROOT / 'pnpm-lock.yaml').exists())
        self.assertFalse((ROOT / 'package-lock.json').exists())

    def test_both_jobs_use_single_frozen_pnpm_path(self):
        ci = (ROOT / '.github/workflows/ci.yml').read_text()
        for job in (ci.split('  build-and-test:', 1)[1].split('  codeql:', 1)[0], ci.split('  release:', 1)[1]):
            self.assertIn('pnpm/action-setup@b906affcce14559ad1aafd4ab0e942779e9f58b1', job)
            self.assertIn('cache: pnpm', job)
            self.assertIn('pnpm install --frozen-lockfile --ignore-scripts', job)
            self.assertIn('pnpm run audit:release', job)
            self.assertNotIn('npm ci', job)
        self.assertIn('pnpm exec semantic-release', ci)


if __name__ == '__main__':
    unittest.main()

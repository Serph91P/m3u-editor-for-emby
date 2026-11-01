"""Real clean-source build checks in disposable linked Git worktrees."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]


class SourceBuildTests(unittest.TestCase):
    def run_fresh_build(self, failing_test=False):
        with tempfile.TemporaryDirectory() as directory:
            checkout = Path(directory) / 'source'
            subprocess.run(['git', 'worktree', 'add', '--detach', str(checkout), 'HEAD'],
                           cwd=ROOT, check=True, capture_output=True)
            try:
                for project in ('Emby.M3uEditor.Plugin', 'Emby.M3uEditor.Plugin.Tests'):
                    self.assertFalse((checkout / project / 'obj').exists())
                    self.assertFalse((checkout / project / 'bin').exists())
                # Exercise the current helper even before its implementation commit.
                (checkout / 'Emby.M3uEditor.Plugin/build.sh').write_bytes(
                    (ROOT / 'Emby.M3uEditor.Plugin/build.sh').read_bytes())
                if failing_test:
                    test = checkout / 'Emby.M3uEditor.Plugin.Tests/UpdateCheckerTests.cs'
                    text = test.read_text().replace('public class UpdateCheckerTests\n    {',
                        'public class UpdateCheckerTests\n    {\n'
                        '        [Fact] public void InjectedSourceBuildFailure() { Assert.Fail("Injected build gate failure"); }')
                    self.assertIn('InjectedSourceBuildFailure', text)
                    test.write_text(text)
                result = subprocess.run(['bash', 'build.sh'], cwd=checkout / 'Emby.M3uEditor.Plugin',
                                        capture_output=True, text=True, timeout=300)
                dll = checkout / 'Emby.M3uEditor.Plugin/out/Emby.M3uEditor.Plugin.dll'
                if failing_test:
                    self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertIn('InjectedSourceBuildFailure', result.stdout)
                    self.assertIn('Failed:', result.stdout)
                    self.assertFalse(dll.exists(), 'Failing tests must prevent publication')
                else:
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertIn('Passed!', result.stdout)
                    self.assertTrue(dll.is_file())
                    self.assertTrue((checkout / 'Emby.M3uEditor.Plugin.Tests/obj/project.assets.json').exists())
                print(result.stdout[-2200:])
            finally:
                subprocess.run(['git', 'worktree', 'remove', '--force', str(checkout)],
                               cwd=ROOT, check=True, capture_output=True)

    def test_documented_fresh_source_build_restores_tests_and_publishes_dll(self):
        self.run_fresh_build()

    def test_real_failing_test_prevents_dll_publication(self):
        self.run_fresh_build(failing_test=True)

    def test_documented_sdk_supports_mandatory_tests_not_a_host_runtime_change(self):
        for name in ('README.md', 'CONTRIBUTING.md'):
            text = (ROOT / name).read_text()
            self.assertIn('.NET SDK 10.0', text)
            self.assertNotIn('SDK 6.0', text)
            self.assertIn('netstandard2.0', text)
            self.assertIn('Emby host', text)


if __name__ == '__main__':
    unittest.main()

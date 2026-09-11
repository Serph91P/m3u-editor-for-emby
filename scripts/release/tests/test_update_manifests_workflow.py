import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).resolve().parents[3] / ".github/workflows/update-manifests.yml"
BOOTSTRAP_STEP = "      - name: Bootstrap gh-pages branch if missing"


def parse_steps(workflow):
    """Read just enough workflow YAML for the source-checkout contract test."""
    steps = []
    current = None
    section = None
    for raw_line in workflow.read_text().splitlines():
        if raw_line.startswith("      - "):
            current = {}
            steps.append(current)
            section = None
            line = raw_line[8:]
        elif current is not None and raw_line.startswith("          "):
            if section is None:
                continue
            line = raw_line[10:]
            key, separator, value = line.partition(":")
            if separator:
                current[section][key.strip()] = value.strip()
            continue
        elif current is not None and raw_line.startswith("        "):
            line = raw_line[8:]
        else:
            continue
        key, separator, value = line.partition(":")
        if not separator:
            continue
        if value.strip():
            current[key.strip()] = value.strip()
            section = None
        else:
            section = key.strip()
            current[section] = {}
    return steps


def resolve_source_ref(ref, event_name, tag_name, sha):
    expression = re.fullmatch(
        r"\$\{\{\s*github\.event_name == 'release' && "
        r"github\.event\.release\.tag_name \|\| github\.sha\s*\}\}", ref)
    return tag_name if expression and event_name == "release" else sha if expression else ref


def bootstrap_shell(workflow_text, remote):
    """Extract the workflow's Bootstrap run body and point it at a local remote."""
    start = workflow_text.index(BOOTSTRAP_STEP)
    run_start = workflow_text.index("        run: |\n", start) + len("        run: |\n")
    next_step = workflow_text.find("\n      - name:", run_start)
    body = workflow_text[run_start : None if next_step == -1 else next_step]
    body = "\n".join(line[10:] if line.startswith("          ") else line for line in body.splitlines())
    return re.sub(
        r'"https://x-access-token:\$\{\{ secrets\.GITHUB_TOKEN \}\}@github\.com/\$\{\{ github\.repository \}\}\.git"',
        f'"{remote}"', body)


class UpdateManifestsWorkflowTests(unittest.TestCase):
    def git(self, *args, cwd=None, check=True):
        return subprocess.run(["git", *args], cwd=cwd, check=check, text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=self.git_env)

    def setUp(self):
        self.tempdir = tempfile.TemporaryDirectory()
        self.root = Path(self.tempdir.name)
        home = self.root / "home"
        home.mkdir()
        self.git_env = {**os.environ, "HOME": str(home), "GIT_CONFIG_NOSYSTEM": "1"}
        self.remote = self.root / "remote.git"
        self.git("init", "--bare", str(self.remote))

    def tearDown(self):
        self.tempdir.cleanup()

    def run_bootstrap(self, workflow_text=None):
        shell = bootstrap_shell(workflow_text or WORKFLOW.read_text(), self.remote)
        return subprocess.run(["bash", "-e", "-c", shell], cwd=self.root, text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=self.git_env)

    def remote_refs(self):
        return self.git("--git-dir", str(self.remote), "for-each-ref", "--format=%(refname)").stdout.splitlines()

    def init_failed_checkout(self):
        pages = self.root / "pages"
        self.git("init", "-b", "master", str(pages))
        self.git("-C", str(pages), "remote", "add", "origin", str(self.remote))
        return pages

    def make_remote_gh_pages(self):
        seed = self.root / "seed"
        self.git("init", "-b", "gh-pages", str(seed))
        (seed / "README.md").write_text("existing gh-pages\n")
        self.git("-C", str(seed), "add", "README.md")
        self.git("-C", str(seed), "-c", "user.name=test", "-c", "user.email=test@example.invalid", "commit", "-m", "seed")
        self.git("-C", str(seed), "remote", "add", "origin", str(self.remote))
        self.git("-C", str(seed), "push", "-u", "origin", "gh-pages")

    def test_source_checkout_ref_matches_release_and_manual_event_contexts(self):
        source_checkouts = [step for step in parse_steps(WORKFLOW)
                            if step.get("uses", "").startswith("actions/checkout@")
                            and step.get("with", {}).get("path") == "source"]
        self.assertEqual(1, len(source_checkouts))
        source_ref = source_checkouts[0]["with"]["ref"]
        for event_name, tag_name, sha, expected in [
            ("release", "v2.5.0-beta.3", "release-commit", "v2.5.0-beta.3"),
            ("workflow_dispatch", None, "manual-selected-branch-commit", "manual-selected-branch-commit"),
        ]:
            with self.subTest(event_name=event_name):
                self.assertEqual(expected, resolve_source_ref(source_ref, event_name, tag_name, sha))

    def test_failed_checkout_bootstraps_only_gh_pages(self):
        pages = self.init_failed_checkout()
        result = self.run_bootstrap()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("gh-pages", self.git("-C", str(pages), "branch", "--show-current").stdout.strip())
        self.assertEqual(["refs/heads/gh-pages"], self.remote_refs())

    def test_existing_gh_pages_checkout_is_untouched(self):
        self.make_remote_gh_pages()
        pages = self.root / "pages"
        self.git("clone", "--branch", "gh-pages", str(self.remote), str(pages))
        before = self.git("-C", str(pages), "rev-parse", "HEAD").stdout.strip()
        refs_before = self.remote_refs()
        result = self.run_bootstrap()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(before, self.git("-C", str(pages), "rev-parse", "HEAD").stdout.strip())
        self.assertEqual(refs_before, self.remote_refs())

    def test_existing_remote_gh_pages_with_wrong_local_branch_fails_without_push(self):
        self.make_remote_gh_pages()
        pages = self.init_failed_checkout()
        refs_before = self.remote_refs()
        result = self.run_bootstrap()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("master", self.git("-C", str(pages), "branch", "--show-current").stdout.strip())
        self.assertEqual(refs_before, self.remote_refs())

    def test_invalid_remote_fails_without_bootstrap(self):
        pages = self.root / "pages"
        self.git("init", "-b", "master", str(pages))
        self.git("-C", str(pages), "remote", "add", "origin", str(self.root / "missing.git"))
        result = self.run_bootstrap()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("master", self.git("-C", str(pages), "branch", "--show-current").stdout.strip())
        self.assertEqual([], self.remote_refs())


if __name__ == "__main__":
    unittest.main()

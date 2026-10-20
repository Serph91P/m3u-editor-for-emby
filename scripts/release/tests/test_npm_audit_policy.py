import copy
import json
import os
import subprocess
import tempfile
import unittest
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
POLICY = ROOT / "audit-ci.json"
ADVISORY = "GHSA-vfj7-8cjw-p6xm"


def report(extra_advisory=None):
    """Synthetic npm v2 report including the release-tooling peer cycle."""
    advisory = {
        "source": 1, "name": "braces", "dependency": "braces",
        "title": "Synthetic nested-pattern advisory",
        "url": f"https://github.com/advisories/{ADVISORY}",
        "severity": "high", "range": "<=3.0.3",
    }
    vulnerabilities = {}
    for name, via, effects in [
        ("braces", [advisory], ["micromatch"]),
        ("micromatch", ["braces"], ["semantic-release", "@semantic-release/commit-analyzer"]),
        ("@semantic-release/commit-analyzer", ["micromatch", "semantic-release"], ["semantic-release"]),
        ("semantic-release", ["micromatch", "@semantic-release/commit-analyzer"], ["@semantic-release/commit-analyzer"]),
    ]:
        vulnerabilities[name] = {
            "name": name, "severity": "high", "isDirect": name == "semantic-release",
            "via": via, "effects": effects, "range": "*",
            "nodes": [f"node_modules/{name}"], "fixAvailable": False,
        }
    if extra_advisory:
        vulnerabilities["braces"]["via"].append(extra_advisory)
    return {
        "auditReportVersion": 2, "vulnerabilities": vulnerabilities,
        "metadata": {"vulnerabilities": {
            "info": 0, "low": 0, "moderate": 0, "high": 4, "critical": 0, "total": 4,
        }},
    }


class NpmAuditPolicyTests(unittest.TestCase):
    def policy(self):
        return json.loads(POLICY.read_text())

    def run_audit(self, audit_report, policy=None, status=1, raw=None):
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            config = directory / "policy.json"
            config.write_text(json.dumps(policy if policy is not None else self.policy()))
            fixture = directory / "report.json"
            fixture.write_text(raw if raw is not None else json.dumps(audit_report))
            npm = directory / "npm"
            npm.write_text(
                "#!/usr/bin/env node\n"
                "const fs = require('node:fs');\n"
                "console.log(fs.readFileSync(process.env.AUDIT_FIXTURE, 'utf8'));\n"
                "process.exitCode = Number(process.env.AUDIT_STATUS);\n"
            )
            npm.chmod(0o755)
            env = os.environ.copy()
            env.update({"PATH": f"{directory}:{env['PATH']}",
                        "AUDIT_FIXTURE": str(fixture), "AUDIT_STATUS": str(status)})
            return subprocess.run(
                ["node", str(ROOT / "node_modules/audit-ci/dist/bin.js"), "--config", str(config)],
                cwd=ROOT, env=env, capture_output=True, text=True, timeout=30,
            )

    def test_policy_keeps_high_gate_dev_dependencies_and_registry_failures(self):
        policy = self.policy()
        self.assertTrue(policy["high"])
        self.assertFalse(policy["skip-dev"])
        self.assertFalse(policy["pass-enoaudit"])
        self.assertEqual(policy["package-manager"], "npm")
        self.assertEqual(len(policy["allowlist"]), 1)
        exception = policy["allowlist"][0]
        self.assertEqual(list(exception), [ADVISORY])
        self.assertTrue(exception[ADVISORY]["active"])
        self.assertEqual(exception[ADVISORY]["expiry"], "2026-11-19T00:00:00Z")
        self.assertIsNotNone(datetime.fromisoformat(exception[ADVISORY]["expiry"]).tzinfo)
        self.assertIn("release", exception[ADVISORY]["notes"].lower())

    def test_accepted_exposure_remains_private_release_only_with_static_patterns(self):
        package = json.loads((ROOT / "package.json").read_text())
        config = json.loads((ROOT / ".releaserc.json").read_text())
        self.assertTrue(package["private"])
        self.assertFalse(package.get("dependencies"))
        self.assertEqual(config["branches"], ["main", {"name": "develop", "prerelease": "beta"}])
        analyzer = next(p[1] for p in config["plugins"] if p[0] == "@semantic-release/commit-analyzer")
        self.assertTrue(all(set(rule) == {"type", "release"} for rule in analyzer["releaseRules"]))
        self.assertTrue(all(rule["type"].isalpha() for rule in analyzer["releaseRules"]))

    def test_accepts_only_approved_advisory_and_derived_peer_cycle(self):
        result = self.run_audit(report())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(ADVISORY, result.stdout + result.stderr)

    def test_accepts_clean_report_after_upstream_fix(self):
        clean = {"auditReportVersion": 2, "vulnerabilities": {}, "metadata": {
            "vulnerabilities": {"info": 0, "low": 0, "moderate": 0, "high": 0, "critical": 0, "total": 0}}}
        result = self.run_audit(clean, status=0)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_rejects_new_high_or_critical_even_in_same_package(self):
        for severity in ("high", "critical"):
            with self.subTest(severity=severity):
                other = copy.deepcopy(report()["vulnerabilities"]["braces"]["via"][0])
                other.update({"source": 2, "severity": severity,
                              "url": "https://github.com/advisories/GHSA-xxxx-yyyy-zzzz"})
                result = self.run_audit(report(other))
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_rejects_expired_or_inactive_exception(self):
        for changes in ({"expiry": "2000-01-01T00:00:00Z"}, {"active": False}):
            with self.subTest(changes=changes):
                policy = self.policy()
                policy["allowlist"][0][ADVISORY].update(changes)
                result = self.run_audit(report(), policy)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_registry_error_is_not_treated_as_success(self):
        result = self.run_audit({"error": {"code": "ENOAUDIT", "summary": "Synthetic registry failure"}}, status=1)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_malformed_registry_output_is_not_treated_as_success(self):
        result = self.run_audit({}, raw="not JSON", status=2)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()

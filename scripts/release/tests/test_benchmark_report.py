"""Offline regressions run the real TypeScript CLI, never a reimplementation."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
TSX = ROOT / "tests/e2e/node_modules/.bin/tsx"


def measured(mode="baseline", pairs=None, cold=None, **metadata):
    return dict(mode=mode, version="stored-version-1.0.0", commit="stored-commit-a1b2c3",
                date="2000-01-01", channels=pairs if pairs is not None else {
                    "Synthetic A": {"cold": cold or {"infoTime": 1, "streamTime": 1},
                                    "warm": [{"infoTime": 1, "streamTime": 1}]}}
                ) | metadata


def pair(info=1, stream=1):
    return dict(infoTime=info, streamTime=stream)


class BenchmarkReportTests(unittest.TestCase):
    def report(self, *files, generated_at=None, env_version="unmeasured-override", cwd=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            shutil.copy(ROOT / "tests/e2e/report.ts", root / "report.ts")
            (root / "results").mkdir()
            for i, data in enumerate(files):
                (root / "results" / f"benchmark-{i}.json").write_text(json.dumps(data))
            command = [str(TSX)]
            if generated_at:
                clock = root / 'clock.cjs'
                clock.write_text('const RealDate = Date; global.Date = class extends RealDate { '
                                 'constructor(...args) { super(...(args.length ? args : [' + json.dumps(generated_at) + '])); } };')
                command += ['--require', str(clock)]
            result = subprocess.run([*command, str(root / "report.ts")], cwd=cwd or ROOT,
                                    env={**os.environ, "PLUGIN_VERSION": env_version},
                                    capture_output=True, text=True, check=True)
            return result.stdout

    def section(self, output, title):
        return output.split("### " + title, 1)[1].split("### ", 1)[0]

    def test_warm_mixed_pairs_exclude_entire_failed_sample(self):
        data = measured()
        data["channels"]["Synthetic A"]["warm"] = [pair(), pair(100, -1), pair()]
        warm = self.section(self.report(data), "Warm Start")
        row = next(line for line in warm.splitlines() if line.startswith("| Synthetic A"))
        self.assertIn("2.00s", row)
        self.assertNotIn("35.00s", row)
        self.assertIn("1 failed", warm)

    def test_warm_valid_failed_empty_and_missing_are_distinct(self):
        data = measured()
        data["channels"]["Failed"] = dict(cold=pair(), warm=[pair(100, -1)])
        data["channels"]["Empty"] = dict(cold=pair(), warm=[])
        warm = self.section(self.report(data), "Warm Start")
        for name, expected in (("Synthetic A", "2.00s"), ("Failed", "ERR"), ("Empty", "--")):
            row = next(line for line in warm.splitlines() if line.startswith("| " + name))
            self.assertIn(expected, row.split("|")[2])
            self.assertEqual("--", row.split("|")[3].strip())
        avg = next(line for line in warm.splitlines() if line.startswith("| **Avg**"))
        self.assertEqual("--", avg.split("|")[3].strip())

    def test_cold_uses_independent_successful_mode_counts(self):
        files = []
        for mode in ("baseline", "with-stats", "no-stats"):
            channels = {name: dict(cold=pair(stream=-1 if mode == "baseline" and name == "C" else 1), warm=[])
                        for name in ("A", "B", "C")}
            files.append(measured(mode, channels))
        phase = self.section(self.report(*files), "Where Time")
        for line in phase.splitlines():
            if line.startswith("| Info dialog") or line.startswith("| Stream start"):
                self.assertEqual(3, line.count("1.00s"), line)
            if line.startswith("| **Total**"):
                self.assertEqual(3, line.count("2.00s"), line)
                self.assertNotIn("+50%", line)

    def test_cold_absent_failed_empty_and_zero_modes(self):
        for baseline in (None, measured(cold=pair(100, -1)), measured(pairs={})):
            files = [measured("with-stats", pairs={name: dict(cold=pair(), warm=[]) for name in ('A', 'B', 'C')})]
            if baseline is not None:
                files.insert(0, baseline)
            output = self.report(*files)
            phase = self.section(output, "Where Time")
            total = next(line for line in phase.splitlines() if line.startswith("| **Total**"))
            self.assertEqual("**ERR**" if baseline and baseline["channels"] else "--", total.split("|")[2].strip())
            self.assertIn("2.00s", total.split("|")[3])
            self.assertEqual("--", total.split("|")[4].strip())
        output = self.report(measured(cold=pair(0, 0)), measured("with-stats", pairs={}),
                             measured("no-stats", cold=pair(100, -1)))
        for title, row_prefix in (("Cold Start", "| **Avg**"), ("Where Time", "| **Total**")):
            row = next(line for line in self.section(output, title).splitlines() if line.startswith(row_prefix))
            self.assertIn("0.00s", row.split("|")[2])
            self.assertEqual("--", row.split("|")[3].strip())
            self.assertEqual("**ERR**", row.split("|")[4].strip())

    def test_provenance_is_stored_not_current_environment_or_checkout(self):
        outputs = [self.report(measured(), generated_at='2030-01-01T00:00:00Z'),
                   self.report(measured(), generated_at='2040-02-03T00:00:00Z',
                               env_version='different-unmeasured-build', cwd=ROOT.parent)]
        for output in outputs:
            for value in ("stored-version-1.0.0", "stored-commit-a1b2c3", "2000-01-01"):
                self.assertIn(value, output)
            self.assertNotIn("unmeasured-override", output)
            self.assertNotIn("different-unmeasured-build", output)
            self.assertIn("Generated", output)
        self.assertIn('2030-01-01', outputs[0])
        self.assertIn('2040-02-03', outputs[1])
        self.assertEqual(self.section(outputs[0], 'Measurement provenance'),
                         self.section(outputs[1], 'Measurement provenance'))

    def test_no_measurements_does_not_report_numeric_zero_or_failures(self):
        output = self.report()
        self.assertIn('No measurements available', output)
        self.assertNotIn('0.00s', output)
        self.assertNotIn('ERR', output)

    def test_mixed_measurements_are_explicit_per_mode(self):
        output = self.report(measured(), measured("with-stats", version="other-build", commit="other-commit", date="2001-02-03"))
        self.assertIn("Mixed measurement provenance", output)
        for value in ("stored-version-1.0.0", "other-build", "other-commit", "2001-02-03"):
            self.assertIn(value, output)


if __name__ == "__main__":
    unittest.main()

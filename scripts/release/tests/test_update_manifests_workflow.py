import re
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).resolve().parents[3] / ".github/workflows/update-manifests.yml"


def parse_steps(workflow):
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
        r"github\.event\.release\.tag_name \|\| github\.sha\s*\}\}",
        ref,
    )
    if expression:
        return tag_name if event_name == "release" else sha
    return ref


class UpdateManifestsWorkflowTests(unittest.TestCase):
    def test_source_checkout_ref_matches_release_and_manual_event_contexts(self):
        source_checkouts = [
            step
            for step in parse_steps(WORKFLOW)
            if step.get("uses", "").startswith("actions/checkout@")
            and step.get("with", {}).get("path") == "source"
        ]
        self.assertEqual(1, len(source_checkouts))
        source_ref = source_checkouts[0]["with"]["ref"]

        cases = [
            ("release", "v2.5.0-beta.3", "release-commit", "v2.5.0-beta.3"),
            ("workflow_dispatch", None, "manual-selected-branch-commit", "manual-selected-branch-commit"),
        ]
        for event_name, tag_name, sha, expected in cases:
            with self.subTest(event_name=event_name):
                self.assertEqual(
                    expected,
                    resolve_source_ref(source_ref, event_name, tag_name, sha),
                )


if __name__ == "__main__":
    unittest.main()

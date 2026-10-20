# Release dependency audit

This private npm project is release tooling, not code shipped in the Emby plugin.
Run `npm ci`, then `npm run audit:release`. The same high/critical audit gate runs
on pull requests and before releases. Development dependencies are included and
registry failures fail closed.

## Temporary exception: GHSA-vfj7-8cjw-p6xm

- Advisory: https://github.com/advisories/GHSA-vfj7-8cjw-p6xm
- Affected package: `braces@3.0.3`, via `micromatch` in `semantic-release` and
  `@semantic-release/commit-analyzer`.
- No patched upstream version is available at the time this exception is added.
- Accepted only for this repository's private release tooling, until
  **2026-11-19 00:00 UTC**. The record expires automatically; there is no module-wide
  exception and no blanket exception for semantic-release or micromatch.
- The vulnerable recursive brace-processing APIs accept deeply nested patterns.
  Our release branches are the literal repository-controlled names `main` and
  `develop`; custom commit-analysis rules use fixed alphabetic `type` patterns.
  Commit text, media data and remote user input do not supply these patterns.
  Do not expand this risk acceptance to arbitrary patterns or application use.
- This is risk acceptance, **not a fix to braces**. Raw `npm audit` continues to
  report the advisory and its derived dependency findings. The CI policy accepts
  only this advisory; a different advisory, including another in braces, fails.

Before expiry, check for a patched braces/micromatch release, refresh the lockfile
and remove the exception once the raw audit is clean. Do not automatically extend
its expiry. If release-pattern sources or npm runtime use change, reassess the
exception instead of relaxing the exposure regression tests.

The npm bundle is pinned through `@semantic-release/npm` because its internal
packages are bundled and cannot be reliably replaced with individual overrides.
Handlebars is pinned to the patched compatible release used for release notes.

Tests use synthetic registry reports with the actual pinned audit-ci CLI to
verify the approved advisory and derived peer cycle, new high/critical advisories
in the same package, expiry/inactivation, clean reports and registry errors.
These fixtures are policy tests, not claims of a real registry audit. Always run
`npm run audit:release` separately against the real registry.

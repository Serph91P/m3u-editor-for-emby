# Release dependency audit

This private Node project is release tooling, not code shipped in the Emby plugin.
Use Node 22 and the pinned `pnpm@10.34.9` from `package.json` (for example via
Corepack). Run `pnpm install --frozen-lockfile --ignore-scripts`, then
`pnpm run audit:release`. Both CI jobs use that same root lockfile and install.
The high/critical audit gate includes development dependencies and fails closed
on registry failures.

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
- This is risk acceptance, **not a fix to braces**. Raw `pnpm audit` is unfiltered:
  when the registry returns this advisory, it remains visible in that report.
  The CI policy accepts only this advisory; a different advisory, including
  another in braces, fails.

Before expiry, check for a patched braces/micromatch release, refresh the lockfile
and remove the exception once the raw audit is clean. Do not automatically extend
its expiry. If release-pattern sources or npm runtime use change, reassess the
exception instead of relaxing the exposure regression tests.

The root `pnpm-workspace.yaml` natively removes only
`semantic-release>@semantic-release/npm`: this repository explicitly configures
its release plugins in `.releaserc.json` and does not publish to npm. Removing
that unused publisher also removes its npm bundle (including bundled vulnerable
undici 6.29.0) and its exclusive `@actions/http-client` / undici 6.29.1 path.
The used GitHub plugin keeps fixed undici 7.30.1. No package, tarball or installed
file is patched, and no audit finding is filtered by this dependency override.
Handlebars stays pinned to 4.7.10 for release notes. Keep Dependabot's ecosystem
as `npm`: it supports the pnpm lockfile.

`node scripts/release/tests/release-toolchain-smoke.mjs` exercises the real
semantic-release configuration loader, every configured plugin, commit analysis
and note generation without invoking publication, authentication or prepare
hooks. It rejects enabling the removed npm publisher; revisit the dependency
removal if npm publishing is ever needed.

Tests use synthetic native pnpm registry reports with the actual pinned audit-ci
CLI to verify the sole approved advisory and dependency paths, each old undici
advisory, unknown high/critical advisories (including development dependencies),
expiry/inactivation, clean reports, malformed/schema-invalid reports and registry
errors. These fixtures are policy tests, not a real registry audit. Always run
`pnpm run audit:release` separately against the real registry.

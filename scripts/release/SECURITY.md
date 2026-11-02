# Release dependency audit

This private Node.js project is release tooling, not code shipped in the Emby plugin.
Use the `packageManager` pin (`pnpm@10.34.9`), run
`pnpm install --frozen-lockfile --ignore-scripts`, then `pnpm run audit:release`.
The same high/critical audit gate runs on pull requests and before releases.
Development dependencies are included; registry failures fail closed and retries
remain disabled. No separate release pipeline or audit bypass is used.

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
- This is risk acceptance, **not a fix to braces**. A clean current audit does not
  make the historical exception a security patch. A different advisory, including
  another in braces, fails.

Before expiry, check for a patched braces/micromatch release, refresh the lockfile
and remove the exception once the raw audit is clean. Do not automatically extend
its expiry. If release-pattern sources or npm publishing use change, reassess the
exception instead of relaxing the exposure regression tests.

## Native removal of unused npm publishing dependencies

The explicit `.releaserc.json` plugin list does not configure npm publishing.
The unused dependency closure is removed using pnpm's documented
`'semantic-release>@semantic-release/npm': '-'` override in
`pnpm-workspace.yaml`. The configured plugin list is unchanged. Handlebars remains
pinned to the patched compatible release used for release notes. No installed
bundle is edited and no stub is substituted. Do not add npm publishing without
revisiting this removal; the real configuration-loader smoke guards that contract.

Fresh frozen installs contain neither `npm` nor `@semantic-release/npm`. The
three old bundled-undici risk exceptions (`GHSA-2m5v-pch6-74hx`,
`GHSA-qrxm-fqhp-jwvm`, `GHSA-xjj6-f33q-929c`) are therefore obsolete and removed.
They were temporary risk acceptance, **not an undici security fix**. Reintroducing
an affected undici version, including only as a development dependency, must fail
the audit on each of those high advisories. The braces exception is unchanged.

Tests use native pnpm-shaped synthetic registry reports with the actual pinned
audit-ci CLI to verify the approved advisory and dependency paths, each former
undici high, unknown high/critical advisories, expiry/inactivation, clean reports,
malformed output and registry errors. These fixtures are policy tests, not a real
registry audit. Always run `pnpm run audit:release` separately against the real
registry. The release-toolchain smoke loads the real configuration and plugins
without invoking prepare or publish hooks.

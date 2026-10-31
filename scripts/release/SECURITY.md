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

## Temporary exceptions: bundled undici

As of 2026-10-31, the native `npm update undici --ignore-scripts` updates the
nonbundled instances to **6.29.1** (`@actions/http-client`) and **7.30.1** (root).
These patched releases remove those affected paths, including
`GHSA-4w7w-5xrc-9h26`; that advisory is **not** allowlisted.

The remaining path is `semantic-release -> @semantic-release/npm -> npm@11.21.1
-> node-gyp@12.4.0 -> undici@6.29.0`. It is bundled by npm. The native
`npm audit fix --ignore-scripts` reports that this bundled dependency cannot be
fixed automatically; the published npm 12.2.1 archive also bundles 6.29.0.
Do not edit installed bundles or use overrides to pretend this path is patched.

The owner explicitly accepted only these three advisories for this private
release tooling until **2026-11-07 00:00 UTC** (`2026-11-07T00:00:00Z`):

- [GHSA-2m5v-pch6-74hx](https://github.com/advisories/GHSA-2m5v-pch6-74hx):
  Agent drops function-valued custom TLS verification options. This repository's
  release tooling does not configure such callbacks; this is not a general claim
  about all npm internals or outbound TLS safety.
- [GHSA-qrxm-fqhp-jwvm](https://github.com/advisories/GHSA-qrxm-fqhp-jwvm):
  a malicious or compromised HTTPS endpoint can crash the client through TLS 1.2
  certificate-changing renegotiation. Outbound tooling requests remain a risk.
- [GHSA-xjj6-f33q-929c](https://github.com/advisories/GHSA-xjj6-f33q-929c):
  a malicious response with excessive content encodings can crash `fetch()`.
  Outbound tooling requests remain a risk; no upstream workaround is documented.

The explicit `.releaserc.json` plugin list excludes `@semantic-release/npm`;
its dependency is nevertheless installed transitively. None of these npm
packages ships in the C# Emby plugin. This limits exposure, not exploitability
of every tooling path. This is **risk acceptance, not an undici security fix**.
Raw `npm audit` continues to report the bundled advisories and derived findings.
There is no blanket undici/module/severity exception. Every record expires
automatically, independently of the unchanged braces exception. Other
high/critical advisories, development dependencies and registry failures remain
blocking. Before expiry, check for a patched npm bundle, update through the native
package manager and remove these records; do not automatically extend them.
Reassess if npm publishing, custom TLS options or tooling exposure changes.

Tests use synthetic registry reports with the actual pinned audit-ci CLI to
verify the approved advisory and derived peer cycle, new high/critical advisories
in the same package, expiry/inactivation, clean reports and registry errors.
These fixtures are policy tests, not claims of a real registry audit. Always run
`npm run audit:release` separately against the real registry.

// semantic-release verifyRelease plugin. It intentionally relies on the
// already-fetched branch/tag metadata supplied by semantic-release rather than
// making a GitHub API call or changing repository state.

const SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

function parseVersion(value) {
  if (typeof value !== "string") return null;
  const match = SEMVER.exec(value);
  if (!match) return null;
  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3]),
    prerelease: match[4],
  };
}

function compareCore(left, right) {
  for (const key of ["major", "minor", "patch"]) {
    if (left[key] !== right[key]) return left[key] > right[key] ? 1 : -1;
  }
  return 0;
}

function stableMainVersion(branches) {
  if (!Array.isArray(branches)) {
    throw new Error("Stable main release metadata is unavailable; refusing beta release.");
  }
  const main = branches.find((branch) => branch && branch.name === "main");
  if (!main || !Array.isArray(main.tags) || main.tags.length === 0) {
    throw new Error("Stable main release metadata is unavailable; refusing beta release.");
  }

  const versions = main.tags.map((tag) => parseVersion(tag && tag.version));
  if (versions.some((version) => version === null)) {
    throw new Error("Stable main release metadata is malformed; refusing beta release.");
  }
  const stable = versions.filter((version) => !version.prerelease);
  if (stable.length === 0) {
    throw new Error("No stable main release tag is available; refusing beta release.");
  }
  return stable.reduce((latest, version) => (compareCore(version, latest) > 0 ? version : latest));
}

export function verifyRelease(_, context) {
  // Stable releases must retain semantic-release's normal behavior.
  if (!context.branch || context.branch.type !== "prerelease" || context.branch.prerelease !== "beta") return;

  const proposed = parseVersion(context.nextRelease && context.nextRelease.version);
  if (!proposed || !proposed.prerelease || !proposed.prerelease.startsWith("beta.")) {
    throw new Error("Proposed beta release version is malformed; refusing release.");
  }

  const latestStable = stableMainVersion(context.branches);
  if (compareCore(proposed, latestStable) <= 0) {
    throw new Error(
      `Refusing ${context.nextRelease.version}: its numeric core does not outrank stable main ` +
        `${latestStable.major}.${latestStable.minor}.${latestStable.patch}. ` +
        "Merge main into develop, then create a new eligible conventional commit so semantic-release computes a higher beta version."
    );
  }
}

import { execFileSync } from 'node:child_process';
import { mkdtemp, rm, symlink, readFile, readdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Writable } from 'node:stream';
import semanticRelease from 'semantic-release';

import { createHash } from 'node:crypto';

const root = fileURLToPath(new URL('../../', import.meta.url));
const digest = bytes => `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
const requireState = (condition, message) => { if (!condition) throw new Error(message); };
const repoPath = `/repos/Serph91P/m3u-editor-for-emby`;

export class GitHub {
  constructor(token, base = 'https://api.github.com', uploads = 'https://uploads.github.com') {
    requireState(token, 'An authenticated repository-write token is required');
    this.token = token; this.base = base; this.uploads = uploads;
  }
  async request(path, { method = 'GET', body, binary = false, upload = false } = {}) {
    const attempts = method === 'GET' ? 3 : 1;
    for (let attempt = 0; attempt < attempts; attempt++) {
      try {
        const response = await fetch(new URL(path, upload ? this.uploads : this.base), {
          method, signal: AbortSignal.timeout(15000),
          headers: { Authorization: `Bearer ${this.token}`, Accept: binary ? 'application/octet-stream' : 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', ...(body ? { 'Content-Type': Buffer.isBuffer(body) ? 'application/octet-stream' : 'application/json' } : {}) },
          body: body ? Buffer.isBuffer(body) ? body : JSON.stringify(body) : undefined,
        });
        if (!response.ok) throw Object.assign(new Error(`GitHub ${method} ${path}: HTTP ${response.status}`), { status: response.status });
        const data = binary ? Buffer.from(await response.arrayBuffer()) : await response.json();
        return { data, link: response.headers.get('link') };
      } catch (error) {
        if (attempt + 1 === attempts || (error.status && error.status < 500) || error instanceof SyntaxError) throw error;
        await new Promise(resolve => setTimeout(resolve, 100 * (attempt + 1)));
      }
    }
  }
  async list(path, key) {
    const values = [];
    let next = `${path}${path.includes('?') ? '&' : '?'}per_page=100`;
    const seen = new Set();
    while (next) {
      requireState(!seen.has(next) && seen.size < 1000, 'Invalid or unbounded GitHub pagination');
      seen.add(next);
      const { data, link } = await this.request(next);
      const items = key ? data[key] : data;
      requireState(Array.isArray(items), 'Malformed GitHub list');
      values.push(...items);
      const target = link?.match(/<([^>]+)>;\s*rel="next"/)?.[1];
      if (target) {
        const url = new URL(target, this.base);
        requireState(url.origin === new URL(this.base).origin && url.pathname === new URL(path, this.base).pathname, 'Untrusted pagination URL');
        next = url.pathname + url.search;
      } else next = null;
    }
    return values;
  }
  async authenticate() {
    const { data } = await this.request(repoPath);
    requireState(data.full_name === repository && data.fork === false && data.permissions?.push === true, 'Cannot prove authenticated draft visibility and repository push authority');
  }
}

async function uncertainWrite(github, path, options) {
  try { await github.request(path, options); }
  catch (error) {
    // A write may already have taken effect. Only readback, never its response, decides progress.
    if (error.status && error.status !== 422 && error.status < 500) throw error;
    if (error instanceof SyntaxError) throw error;
  }
}

export async function publishRelease({ github, plan, assets }) {
  await github.authenticate();
  const expected = { tag_name: plan.gitTag, target_commitish: plan.gitHead, name: plan.gitTag, body: plan.notes, prerelease: plan.channel != null };
  const find = async () => {
    const matches = (await github.list(`${repoPath}/releases`)).filter(release => release.tag_name === plan.gitTag);
    requireState(matches.length <= 1, `Duplicate releases for ${plan.gitTag}`);
    const release = matches[0];
    if (release) {
      requireState(Number.isSafeInteger(release.id) && typeof release.draft === 'boolean', 'Malformed release identity');
      for (const [key, value] of Object.entries(expected)) requireState(release[key] === value, `Conflicting release ${key} for ${plan.gitTag}`);
    }
    return release;
  };
  let release;
  for (let attempt = 0; attempt < 3; attempt++) {
    release = await find();
    if (release) break;
    await uncertainWrite(github, `${repoPath}/releases`, { method: 'POST', body: { ...expected, draft: true } });
  }
  release = await find();
  requireState(release, 'Release creation did not converge after three attempts');
  const verify = async () => {
    const remote = await github.list(`${repoPath}/releases/${release.id}/assets`);
    const names = new Set();
    for (const asset of remote) {
      requireState(Number.isSafeInteger(asset.id) && !names.has(asset.name), 'Malformed or duplicate release asset');
      names.add(asset.name);
      const bytes = assets.get(asset.name);
      requireState(bytes && bytes.length > 0 && asset.state === 'uploaded' && asset.size === bytes.length && (!asset.digest || asset.digest === digest(bytes)), `Conflicting or incomplete asset ${asset.name}; no assets will be deleted`);
      const downloaded = (await github.request(`${repoPath}/releases/assets/${asset.id}`, { binary: true })).data;
      requireState(downloaded.equals(bytes), `Conflicting asset bytes: ${asset.name}`);
    }
    return [...assets.keys()].filter(name => !names.has(name));
  };
  let missing = await verify();
  requireState(release.draft || !missing.length, 'Published release is incomplete; refusing mutation');
  for (const name of missing) {
    for (let attempt = 0; attempt < 3 && (await verify()).includes(name); attempt++) {
      await uncertainWrite(github, `${repoPath}/releases/${release.id}/assets?name=${encodeURIComponent(name)}`, { method: 'POST', body: assets.get(name), upload: true });
    }
    requireState(!(await verify()).includes(name), `Asset upload did not converge: ${name}`);
  }
  requireState(!(await verify()).length, 'Incomplete release asset readback');
  for (let attempt = 0; attempt < 3; attempt++) {
    release = await find();
    if (!release.draft) break;
    await uncertainWrite(github, `${repoPath}/releases/${release.id}`, { method: 'PATCH', body: { draft: false } });
  }
  release = await find();
  requireState(release && !release.draft && !(await verify()).length, 'Published release readback failed');
  return release;
}
const repository = 'Serph91P/m3u-editor-for-emby';
const repositoryUrl = `https://github.com/${repository}.git`;
const gitEnv = { ...process.env, GIT_TERMINAL_PROMPT: '0', GIT_AUTHOR_NAME: 'release', GIT_AUTHOR_EMAIL: 'release@example.invalid', GIT_COMMITTER_NAME: 'release', GIT_COMMITTER_EMAIL: 'release@example.invalid' };
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: gitEnv, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const ancestor = (cwd, a, b) => { try { git(cwd, 'merge-base', '--is-ancestor', a, b); return true; } catch (error) { if (error.status === 1) return false; throw error; } };
const config = JSON.parse(await readFile(join(root, '.releaserc.json'), 'utf8'));
const semver = createRequire(import.meta.url)(createRequire(import.meta.url).resolve('semver', { paths: [fileURLToPath(new URL('.', import.meta.resolve('semantic-release')))] }));

const sourceConfig = (cwd, source) => JSON.parse(git(cwd, 'show', `${source}:.releaserc.json`));
const notesRefs = cwd => git(cwd, 'for-each-ref', '--format=%(refname)', 'refs/notes').split('\n').filter(ref => /^refs\/notes\/semantic-release(?:-|$)/.test(ref));

function checkNotes(cwd, plan) {
  let found = false;
  for (const ref of notesRefs(cwd)) {
    const entries = git(cwd, 'notes', `--ref=${ref}`, 'list').split('\n').filter(Boolean);
    const entry = entries.find(line => line.split(' ')[1] === plan.gitHead);
    if (!entry) continue;
    const note = JSON.parse(git(cwd, 'cat-file', '-p', entry.split(' ')[0]));
    requireState(Array.isArray(note.channels) && note.channels.length === 1 && note.channels[0] === (plan.channel ?? null), `Conflicting semantic-release notes for ${plan.gitTag}`);
    found = true;
  }
  return found;
}

async function remoteNotes(cwd, plan) {
  const dir = await mkdtemp(join(tmpdir(), 'release-notes-readback-'));
  try {
    git(dir, 'init', '--bare');
    git(dir, 'fetch', '--no-tags', git(cwd, 'remote', 'get-url', 'origin'), `refs/tags/${plan.gitTag}:refs/tags/${plan.gitTag}`, 'refs/notes/*:refs/notes/*');
    return checkNotes(dir, plan);
  } finally { await rm(dir, { recursive: true, force: true }); }
}

export async function verifyHistoricalSource({ github, source, branch }) {
  const { data: workflow } = await github.request(`${repoPath}/actions/workflows/ci.yml`);
  requireState(workflow.path === '.github/workflows/ci.yml' && workflow.state === 'active' && Number.isSafeInteger(workflow.id), 'Untrusted CI workflow identity');
  const runs = await github.list(`${repoPath}/actions/workflows/ci.yml/runs?head_sha=${source}&event=push&branch=${branch}`, 'workflow_runs');
  for (const run of runs) {
    if (run.workflow_id !== workflow.id || run.path !== workflow.path || run.head_sha !== source || run.head_branch !== branch || run.event !== 'push' || run.head_repository?.full_name !== repository || run.head_repository?.fork !== false || run.status !== 'completed') continue;
    const jobs = await github.list(`${repoPath}/actions/runs/${run.id}/jobs?filter=latest`, 'jobs');
    if (['Build & Test', 'CodeQL Security Analysis'].every(name => jobs.filter(job => job.name === name).length === 1 && jobs.some(job => job.name === name && job.head_sha === source && job.conclusion === 'success'))) return;
  }
  throw new Error(`No trusted push CI build and CodeQL success at artifact source ${source}`);
}

export async function buildBundle({ cwd, plan }) {
  const dir = await mkdtemp(join(tmpdir(), 'release-build-'));
  try {
    git(dir, 'clone', '--no-checkout', '--no-hardlinks', cwd, 'source');
    const source = join(dir, 'source');
    git(source, 'checkout', '--detach', plan.gitHead);
    requireState(git(source, 'rev-parse', 'HEAD') === plan.gitHead, 'Artifact source checkout mismatch');
    const cfg = sourceConfig(cwd, plan.gitHead);
    const command = cfg.plugins.find(([name]) => name === '@semantic-release/exec')?.[1]?.prepareCmd;
    requireState(typeof command === 'string' && command === config.plugins.find(([name]) => name === '@semantic-release/exec')[1].prepareCmd, 'Unsupported historical artifact preparation contract');
    // The reviewed controller supplies packaging; product files remain the exact source checkout.
    const prepare = command.replaceAll('${nextRelease.version}', plan.version).replaceAll('scripts/release/', `${root}scripts/release/`);
    execFileSync('bash', ['-e', '-o', 'pipefail', '-c', prepare], { cwd: source, env: { ...process.env, RELEASE_SOURCE_SHA: plan.gitHead }, stdio: ['ignore', 'pipe', 'pipe'], timeout: 180000 });
    const files = await readdir(join(source, 'artifacts'));
    const definitions = cfg.plugins.find(([name]) => name === '@semantic-release/github')?.[1]?.assets;
    requireState(JSON.stringify(definitions) === JSON.stringify(config.plugins.find(([name]) => name === '@semantic-release/github')[1].assets), 'Historical assets contract differs');
    const assets = new Map();
    for (const { path } of definitions) {
      const pattern = new RegExp(`^${path.replace('artifacts/', '').replaceAll('.', '\\.').replaceAll('*', '.*')}$`);
      const matches = files.filter(name => pattern.test(name));
      requireState(matches.length === 1, `Missing or ambiguous configured asset ${path}`);
      assets.set(matches[0], await readFile(join(source, 'artifacts', matches[0])));
    }
    requireState(assets.size === 4, 'Incomplete configured release bundle');
    return assets;
  } finally { await rm(dir, { recursive: true, force: true }); }
}

export async function runRelease({ cwd = process.cwd(), env = process.env, github = new GitHub(env.GITHUB_TOKEN) } = {}) {
  const branch = env.GITHUB_REF_NAME, controller = env.GITHUB_SHA;
  const authority = () => {
    execFileSync('bash', [join(root, 'scripts/release/verify-release-context.sh')], { cwd, env, stdio: ['ignore', 'pipe', 'pipe'] });
    requireState(git(cwd, 'rev-parse', 'HEAD') === controller, 'Controller HEAD differs from CI source');
    const origin = git(cwd, 'config', '--get', 'remote.origin.url');
    requireState([repositoryUrl, `git@github.com:${repository}.git`, `ssh://git@github.com/${repository}.git`].includes(origin), 'Untrusted origin repository');
  };
  authority();
  await github.authenticate();
  git(cwd, 'fetch', 'origin', 'refs/heads/main:refs/remotes/origin/main', 'refs/heads/develop:refs/remotes/origin/develop', 'refs/tags/*:refs/tags/*', 'refs/notes/*:refs/notes/*');
  const releases = await github.list(`${repoPath}/releases`);
  const tags = git(cwd, 'tag', '--merged', controller).split('\n').filter(tag => {
    const version = semver.valid(tag);
    return version && (branch === 'develop' ? semver.prerelease(version)?.[0] === 'beta' : !semver.prerelease(version));
  }).sort((a, b) => semver.compare(a, b));
  const incomplete = tags.filter(tag => !releases.some(release => release.tag_name === tag && release.draft === false));
  // Published releases do not hide an earlier interrupted candidate, including notes-present tags.
  let plan;
  if (incomplete.length) {
    requireState(incomplete.length === 1, 'Multiple unresolved release tags; refusing version advance');
    const tag = incomplete[0], source = git(cwd, 'rev-parse', `${tag}^{commit}`);
    requireState(ancestor(cwd, source, controller), 'Release tag is not a controller ancestor');
    plan = await planRelease({ cwd, source, branch, excludeTag: tag });
    requireState(plan && plan.gitTag === tag && plan.gitHead === source && plan.version === semver.valid(tag), 'Historical native plan does not reproduce candidate identity');
  } else {
    plan = await planRelease({ cwd, source: controller, branch });
    if (!plan && tags.length) {
      const tag = tags.at(-1), source = git(cwd, 'rev-parse', `${tag}^{commit}`);
      plan = await planRelease({ cwd, source, branch, excludeTag: tag });
      requireState(plan && plan.gitTag === tag && plan.gitHead === source, 'Published native plan cannot be reconstructed');
    }
  }
  if (!plan) { console.log('No release planned.'); return; }
  requireState(semver.valid(plan.version) && plan.gitTag === `v${plan.version}`, 'Invalid native release identity');
  requireState(plan.channel === (branch === 'develop' ? 'develop' : null), 'Unexpected native channel');
  const hasNotes = checkNotes(cwd, plan);
  if (plan.gitHead !== controller) await verifyHistoricalSource({ github, source: plan.gitHead, branch });
  const assets = await buildBundle({ cwd, plan });
  authority();
  let remoteTag = git(cwd, 'ls-remote', 'origin', `refs/tags/${plan.gitTag}`, `refs/tags/${plan.gitTag}^{}`);
  if (!remoteTag) {
    git(cwd, 'tag', plan.gitTag, plan.gitHead);
    try { git(cwd, 'push', 'origin', `refs/tags/${plan.gitTag}`); } catch { /* Confirm an uncertain push by exact remote readback below. */ }
  }
  remoteTag = git(cwd, 'ls-remote', 'origin', `refs/tags/${plan.gitTag}`, `refs/tags/${plan.gitTag}^{}`);
  requireState(remoteTag.split('\n').at(-1)?.split(/\s+/)[0] === plan.gitHead, 'Remote tag source mismatch');
  if (!(await remoteNotes(cwd, plan))) {
    if (!hasNotes) git(cwd, 'notes', `--ref=semantic-release-${plan.gitTag}`, 'add', '-m', JSON.stringify({ channels: [plan.channel ?? null] }), plan.gitTag);
    try { git(cwd, 'push', 'origin', `refs/notes/semantic-release-${plan.gitTag}`); } catch { /* An independent remote snapshot verifies an uncertain non-force push. */ }
  }
  requireState(await remoteNotes(cwd, plan), 'Remote semantic-release notes readback failed');
  authority();
  const release = await publishRelease({ github, plan, assets });
  authority();
  console.log(`Verified published ${plan.gitTag} at ${plan.gitHead} (release ${release.id}); controller ${controller}`);
  return { plan, release };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  runRelease().catch(error => { console.error(error.message); process.exitCode = 1; });
}

// Only this disposable snapshot hides a candidate tag; origin and CI identity stay untouched.
export async function planRelease({ cwd, source, branch, excludeTag }) {
  const cfg = sourceConfig(cwd, source);
  const dir = await mkdtemp(join(tmpdir(), 'release-plan-'));
  try {
    const remote = join(dir, 'remote.git');
    const snapshot = join(dir, 'snapshot');
    git(dir, 'init', '--bare', remote);
    git(remote, 'fetch', '--no-tags', cwd, source);
    git(remote, 'update-ref', `refs/heads/${branch}`, source);
    git(remote, 'symbolic-ref', 'HEAD', `refs/heads/${branch}`);
    if (branch === 'develop') {
      const main = git(cwd, 'rev-parse', 'refs/remotes/origin/main');
      if (!ancestor(cwd, main, source)) throw new Error('Historical main branch cannot be proven from source ancestry');
      git(remote, 'fetch', '--no-tags', cwd, main);
      git(remote, 'update-ref', 'refs/heads/main', main);
    }
    for (const tag of git(cwd, 'tag', '--merged', source).split('\n').filter(Boolean)) {
      if (tag !== excludeTag) git(remote, 'fetch', '--no-tags', cwd, `refs/tags/${tag}:refs/tags/${tag}`);
    }
    for (const ref of git(cwd, 'for-each-ref', '--format=%(refname)', 'refs/notes').split('\n').filter(ref => ref.startsWith('refs/notes/semantic-release'))) {
      git(remote, 'fetch', '--no-tags', cwd, `${ref}:${ref}`);
    }
    git(dir, 'clone', '--branch', branch, remote, snapshot);
    git(snapshot, 'config', `url.${remote}.insteadOf`, repositoryUrl);
    git(snapshot, 'remote', 'set-url', 'origin', repositoryUrl);
    await symlink(join(root, 'node_modules'), join(snapshot, 'node_modules'), 'dir');
    const releaseDate = git(cwd, 'show', '-s', '--format=%cs', source);
    const plugins = cfg.plugins.filter(([name]) => !['@semantic-release/exec', '@semantic-release/github'].includes(name))
      .map(([name, options]) => [name.startsWith('./') ? join(snapshot, name) : name, name === '@semantic-release/release-notes-generator'
        ? { ...options, writerOpts: { ...options?.writerOpts, formatDate: () => releaseDate } } : options || {}]);
    const sink = new Writable({ write(chunk, encoding, done) { done(); } });
    // Explicit non-CI environment belongs only to a local, non-publishing planner.
    const env = { PATH: process.env.PATH, HOME: process.env.HOME, TMPDIR: process.env.TMPDIR, ...Object.fromEntries(Object.entries(gitEnv).filter(([key]) => key.startsWith('GIT_'))) };
    const result = await semanticRelease({ dryRun: true, repositoryUrl, branches: cfg.branches, tagFormat: cfg.tagFormat, plugins }, { cwd: snapshot, env, stdout: sink, stderr: sink });
    return result?.nextRelease || null;
  } finally { await rm(dir, { recursive: true, force: true }); }
}

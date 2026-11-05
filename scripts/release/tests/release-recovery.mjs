import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, rm, writeFile, readFile, mkdir, copyFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';
import { createHash } from 'node:crypto';

async function githubFixture(failure) {
  const releases = [];
  const runs = [];
  let authority = { full_name: 'Serph91P/m3u-editor-for-emby', fork: false, permissions: { push: true } };
  let nextId = 1, injected = false, writes = 0;
  const server = createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const path = url.pathname.replace('/repos/Serph91P/m3u-editor-for-emby', '');
    const send = (status, data) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(data)); };
    const chunks = []; for await (const chunk of req) chunks.push(chunk);
    const bytes = Buffer.concat(chunks);
    if (req.headers.authorization !== 'Bearer synthetic-token') return send(403, {});
    if (path === '' && req.method === 'GET') return send(200, authority);
    if (path === '/actions/workflows/ci.yml') return send(200, { id: 42, path: '.github/workflows/ci.yml', state: 'active' });
    if (path === '/actions/workflows/ci.yml/runs') return send(200, { workflow_runs: runs.map(({ jobs, ...run }) => run) });
    if (/^\/actions\/runs\/\d+\/jobs$/.test(path)) return send(200, { jobs: runs.find(run => run.id === Number(path.split('/')[3])).jobs });
    const operation = req.method === 'POST' && path === '/releases' ? 'create' : req.method === 'POST' ? 'upload' : req.method === 'PATCH' ? 'publish' : null;
    if (operation) writes++;
    const fail = operation === failure?.operation && !injected;
    if (fail && !failure.after) { injected = true; return send(failure.status, {}); }
    let result;
    if (req.method === 'GET' && path === '/releases') {
      const page = Number(url.searchParams.get('page') || 1);
      if (page === 1) res.setHeader('Link', `<http://127.0.0.1:${server.address().port}${url.pathname}?page=2>; rel="next"`);
      return send(200, page === 1 ? [] : releases.map(({ assets, ...release }) => release));
    }
    if (operation === 'create') {
      const input = JSON.parse(bytes);
      result = { ...input, id: nextId++, assets: [] };
      releases.push(result);
    } else if (operation === 'upload') {
      const release = releases.find(r => r.id === Number(path.split('/')[2]));
      const name = url.searchParams.get('name');
      if (release.assets.some(a => a.name === name)) return send(422, {});
      result = { id: nextId++, name, size: bytes.length, state: 'uploaded', digest: `sha256:${createHash('sha256').update(bytes).digest('hex')}`, bytes };
      release.assets.push(result);
    } else if (operation === 'publish') {
      result = releases.find(r => r.id === Number(path.split('/')[2]));
      Object.assign(result, JSON.parse(bytes));
    } else if (req.method === 'GET' && /\/assets$/.test(path)) {
      const release = releases.find(r => r.id === Number(path.split('/')[2]));
      const page = Number(url.searchParams.get('page') || 1);
      if (page === 1) res.setHeader('Link', `<http://127.0.0.1:${server.address().port}${url.pathname}?page=2>; rel="next"`);
      return send(200, page === 1 ? [] : release.assets.map(({ bytes, ...asset }) => asset));
    } else if (req.method === 'GET' && path.startsWith('/releases/assets/')) {
      const asset = releases.flatMap(r => r.assets || []).find(a => a.id === Number(path.split('/')[3]));
      res.writeHead(200, { 'Content-Type': 'application/octet-stream' }); return res.end(asset.bytes);
    } else return send(404, {});
    if (fail) { injected = true; if (failure.status === 'disconnect') return req.socket.destroy(); return send(failure.status, {}); }
    return send(200, result);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return { releases, runs, authority, writes: () => writes, base: `http://127.0.0.1:${server.address().port}`, close: () => new Promise(resolve => server.close(resolve)) };
}

for (const failure of [null, ...['create', 'upload', 'publish'].flatMap(operation => [
  { operation, after: false, status: 500 }, { operation, after: true, status: 'disconnect' }, { operation, after: true, status: 422 },
])]) {
  test(`HTTP publisher converges with ${JSON.stringify(failure)}, paginated readback and immutable no-op`, async () => {
    const f = await githubFixture(failure);
    try {
      const { GitHub, publishRelease } = await import('../release.mjs');
      assert.equal(typeof publishRelease, 'function', 'restart-safe publisher is required');
      const github = new GitHub('synthetic-token', f.base, f.base);
      const plan = { gitTag: 'v1.0.1-beta.1', version: '1.0.1-beta.1', channel: 'develop', gitHead: 'a'.repeat(40), notes: 'native notes' };
      const assets = new Map([['synthetic.dll', Buffer.from('synthetic dll')], ['synthetic.zip', Buffer.from('synthetic zip')]]);
      await publishRelease({ github, plan, assets });
      assert.equal(f.releases.length, 1);
      assert.equal(f.releases[0].draft, false);
      assert.equal(f.releases[0].assets.length, 2);
      for (const asset of f.releases[0].assets) assert.deepEqual(asset.bytes, assets.get(asset.name));
      const writes = f.writes();
      await publishRelease({ github, plan, assets });
      assert.equal(f.writes(), writes, 'published rerun must not mutate anything');
    } finally { await f.close(); }
  });
}


for (const mutation of ['sha', 'notes', 'channel', 'version', 'bytes', 'starter', 'empty', 'published-incomplete', 'duplicate']) {
  test(`conflicting ${mutation} is fail-closed without changing assets`, async () => {
    const f = await githubFixture();
    try {
      const { GitHub, publishRelease } = await import('../release.mjs');
      const github = new GitHub('synthetic-token', f.base, f.base);
      const plan = { gitTag: 'v1.0.1', version: '1.0.1', channel: null, gitHead: 'a'.repeat(40), notes: 'native notes' };
      const assets = new Map([['one.dll', Buffer.from('valid bytes')]]);
      await publishRelease({ github, plan, assets });
      const release = f.releases[0];
      if (mutation === 'sha') release.target_commitish = 'b'.repeat(40);
      if (mutation === 'notes') release.body = 'foreign';
      if (mutation === 'channel') release.prerelease = true;
      if (mutation === 'version') release.name = 'v9.0.0';
      if (mutation === 'bytes') { release.assets[0].bytes = Buffer.from('other bytes'); delete release.assets[0].digest; }
      if (mutation === 'starter') { release.draft = true; release.assets[0].state = 'starter'; }
      if (mutation === 'empty') { release.draft = true; release.assets[0].size = 0; }
      if (mutation === 'published-incomplete') release.assets = [];
      if (mutation === 'duplicate') release.assets.push({ ...release.assets[0] });
      const before = JSON.stringify(f.releases), writes = f.writes();
      await assert.rejects(publishRelease({ github, plan, assets }), /Conflicting|incomplete|duplicate/);
      assert.equal(f.writes(), writes);
      assert.equal(JSON.stringify(f.releases), before);
    } finally { await f.close(); }
  });
}

for (const status of [403, 500, 'malformed']) {
  test(`read failure ${status} cannot authorize creation`, async () => {
    let requests = 0;
    const server = createServer((req, res) => {
      requests++;
      res.writeHead(status === 'malformed' ? 200 : status, { 'Content-Type': 'application/json' });
      res.end(status === 'malformed' ? '{' : '{}');
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
      const { GitHub, publishRelease } = await import('../release.mjs');
      const github = new GitHub('synthetic-token', `http://127.0.0.1:${server.address().port}`);
      await assert.rejects(publishRelease({ github, plan: {}, assets: new Map() }));
      assert.equal(requests, status === 500 ? 3 : 1);
    } finally { await new Promise(resolve => server.close(resolve)); }
  });
}

const root = fileURLToPath(new URL('../../../', import.meta.url));
const git = (cwd, ...args) => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
async function fixture(branch = 'develop', product = false) {
  const dir = await mkdtemp(join(tmpdir(), 'release-recovery-'));
  const cwd = join(dir, 'source');
  await mkdir(cwd);
  if (product) execFileSync('tar', ['-x', '-C', cwd], { input: execFileSync('git', ['archive', 'HEAD'], { cwd: root, maxBuffer: 30 * 1024 * 1024 }) });
  await mkdir(join(cwd, 'scripts/release'), { recursive: true });
  await copyFile(join(root, '.releaserc.json'), join(cwd, '.releaserc.json'));
  await copyFile(join(root, 'scripts/release/verify-beta-version-floor.mjs'), join(cwd, 'scripts/release/verify-beta-version-floor.mjs'));
  git(dir, 'init', '--initial-branch=main', cwd);
  git(cwd, 'config', 'user.name', 'Fixture');
  git(cwd, 'config', 'user.email', 'fixture@example.invalid');
  await writeFile(join(cwd, 'fixture'), 'base');
  git(cwd, 'add', '.'); git(cwd, 'commit', '-m', 'feat: initial synthetic version');
  git(cwd, 'tag', 'v1.0.0');
  const stable = git(cwd, 'rev-parse', 'HEAD');
  if (branch === 'develop') git(cwd, 'checkout', '-b', branch);
  await writeFile(join(cwd, 'fixture'), 'fix');
  git(cwd, 'commit', '-am', 'fix: synthetic recovery');
  const source = git(cwd, 'rev-parse', 'HEAD');
  if (branch === 'main') git(cwd, 'branch', 'develop');
  const remote = join(dir, 'remote.git');
  git(dir, 'clone', '--bare', cwd, remote);
  git(cwd, 'remote', 'add', 'origin', remote);
  git(cwd, 'fetch', 'origin', '+refs/heads/*:refs/remotes/origin/*');
  return { dir, cwd, source, remote, stable, branch, close: () => rm(dir, { recursive: true, force: true }) };
}

for (const scenario of ['main-fresh', 'develop-fresh', 'tag-only-old-source', 'notes-only-old-source', 'notes-push-failure']) {
  test(`real runner and builder: ${scenario}, immutable source and verified rerun`, { timeout: 180000 }, async () => {
    const branch = scenario.startsWith('main') ? 'main' : 'develop';
    const f = await fixture(branch, true), http = await githubFixture({ operation: 'upload', after: true, status: 'disconnect' });
    try {
      const { GitHub, runRelease, planRelease } = await import('../release.mjs');
      assert.equal(typeof runRelease, 'function', 'real release controller is required');
      const initial = await planRelease({ cwd: f.cwd, source: f.source, branch });
      http.releases.push({ id: 900, tag_name: 'v1.0.0', draft: false });
      if (scenario.includes('old-source')) {
        git(f.cwd, 'tag', initial.gitTag, f.source);
        git(f.cwd, 'push', 'origin', `refs/tags/${initial.gitTag}`);
        if (scenario.startsWith('notes')) {
          git(f.cwd, 'notes', `--ref=semantic-release-${initial.gitTag}`, 'add', '-m', JSON.stringify({ channels: [initial.channel] }), initial.gitTag);
          git(f.cwd, 'push', 'origin', `refs/notes/semantic-release-${initial.gitTag}`);
        }
        await writeFile(join(f.cwd, 'fixture'), 'new controller, not artifact source');
        git(f.cwd, 'commit', '-am', 'ci: synthetic controller fix');
        git(f.cwd, 'push', 'origin', branch);
        http.runs.push({ id: 100, workflow_id: 42, path: '.github/workflows/ci.yml', head_sha: f.source, head_branch: branch, event: 'push', head_repository: { full_name: 'Serph91P/m3u-editor-for-emby', fork: false }, status: 'completed', conclusion: 'failure', jobs: [
          { name: 'Build & Test', conclusion: 'success', head_sha: f.source },
          { name: 'CodeQL Security Analysis', conclusion: 'success', head_sha: f.source },
          { name: 'Release', conclusion: 'failure', head_sha: f.source },
        ] });
      }
      const controller = git(f.cwd, 'rev-parse', 'HEAD');
      // Only a disposable Git remote is redirected; the real guard sees the canonical identity.
      git(f.cwd, 'config', `url.${f.remote}.insteadOf`, 'https://github.com/Serph91P/m3u-editor-for-emby.git');
      git(f.cwd, 'remote', 'set-url', 'origin', 'https://github.com/Serph91P/m3u-editor-for-emby.git');
      const env = { ...process.env, GITHUB_EVENT_NAME: 'push', GITHUB_REPOSITORY: 'Serph91P/m3u-editor-for-emby', GITHUB_REF_NAME: branch, GITHUB_REF: `refs/heads/${branch}`, GITHUB_SHA: controller, RELEASE_BUILD_RESULT: 'success', RELEASE_CODEQL_RESULT: 'success' };
      const github = new GitHub('synthetic-token', http.base, http.base);
      if (scenario === 'notes-push-failure') {
        const hook = join(f.remote, 'hooks/pre-receive');
        await writeFile(hook, '#!/bin/sh\nwhile read old new ref; do\n case "$ref" in refs/notes/*) exit 1;; esac\ndone\n');
        execFileSync('chmod', ['+x', hook]);
        await assert.rejects(runRelease({ cwd: f.cwd, env, github }), /notes push|notes readback/);
        assert.equal(http.writes(), 0);
        assert.equal(git(f.remote, 'rev-parse', initial.gitTag), f.source);
        await rm(hook);
      }
      await runRelease({ cwd: f.cwd, env, github });
      const release = http.releases.find(r => r.tag_name === initial.gitTag);
      assert.equal(release.target_commitish, f.source);
      assert.equal(release.draft, false);
      assert.equal(release.assets.length, 4);
      assert.equal(git(f.remote, 'rev-parse', `${initial.gitTag}^{commit}`), f.source);
      assert.deepEqual(JSON.parse(git(f.remote, 'notes', `--ref=semantic-release-${initial.gitTag}`, 'show', initial.gitTag)), { channels: [initial.channel] });
      assert.equal(env.GITHUB_SHA, controller);
      assert.equal(git(f.cwd, 'rev-parse', 'HEAD'), controller);
      const before = JSON.stringify(http.releases), writes = http.writes();
      await runRelease({ cwd: f.cwd, env, github });
      assert.equal(http.writes(), writes);
      assert.equal(JSON.stringify(http.releases), before);
      assert.equal(git(f.cwd, 'status', '--porcelain'), '');
    } finally { await f.close(); await http.close(); }
  });
}

test('unresolved legacy tags below a published successor require an explicit recovery boundary', async () => {
  const f = await fixture('develop'), http = await githubFixture();
  try {
    const { GitHub, runRelease, planRelease } = await import('../release.mjs');
    const plan = await planRelease({ cwd: f.cwd, source: f.source, branch: 'develop' });
    git(f.cwd, 'tag', 'v0.9.1-beta.1', f.stable);
    git(f.cwd, 'tag', plan.gitTag, f.source);
    git(f.cwd, 'push', 'origin', '--tags');
    http.releases.push({ id: 900, tag_name: 'v1.0.0', draft: false }, { id: 901, tag_name: 'v1.0.0-beta.1', draft: false });
    git(f.cwd, 'config', `url.${f.remote}.insteadOf`, 'https://github.com/Serph91P/m3u-editor-for-emby.git');
    git(f.cwd, 'remote', 'set-url', 'origin', 'https://github.com/Serph91P/m3u-editor-for-emby.git');
    const env = { ...process.env, GITHUB_EVENT_NAME: 'push', GITHUB_REPOSITORY: 'Serph91P/m3u-editor-for-emby', GITHUB_REF_NAME: 'develop', GITHUB_REF: 'refs/heads/develop', GITHUB_SHA: f.source, RELEASE_BUILD_RESULT: 'success', RELEASE_CODEQL_RESULT: 'success' };
    await assert.rejects(runRelease({ cwd: f.cwd, env, github: new GitHub('synthetic-token', http.base, http.base) }), /Multiple unresolved release tags/);
    assert.equal(http.writes(), 0);
    assert.equal(git(f.remote, 'rev-parse', plan.gitTag), f.source);
    assert.equal(http.releases.length, 2);
  } finally { await f.close(); await http.close(); }
});

for (const branch of ['main', 'develop']) {
  test(`native planner computes ${branch}, reconstructs orphan and recognizes v25 notes`, async () => {
    const f = await fixture(branch);
    try {
      const runnerUrl = new URL('../release.mjs', import.meta.url);
      const { planRelease } = await import(runnerUrl);
      const expected = branch === 'main' ? '1.0.1' : '1.0.1-beta.1';
      const plan = await planRelease({ cwd: f.cwd, source: f.source, branch });
      assert.equal(plan.version, expected);
      assert.equal(plan.channel, branch === 'main' ? null : 'develop');
      assert.equal(plan.gitHead, f.source);
      assert.match(plan.notes, /synthetic recovery/);
      assert.match(plan.notes, /github.com\/Serph91P\/m3u-editor-for-emby/);
      git(f.cwd, 'tag', plan.gitTag, f.source);
      git(f.cwd, 'push', 'origin', `refs/tags/${plan.gitTag}`);
      const recovered = await planRelease({ cwd: f.cwd, source: f.source, branch, excludeTag: plan.gitTag });
      assert.deepEqual(recovered, plan);
      git(f.cwd, 'notes', `--ref=semantic-release-${plan.gitTag}`, 'add', '-m', JSON.stringify({ channels: [plan.channel] }), plan.gitTag);
      git(f.cwd, 'push', 'origin', `refs/notes/semantic-release-${plan.gitTag}`);
      assert.equal(await planRelease({ cwd: f.cwd, source: f.source, branch }), null);
      assert.equal(git(f.remote, 'rev-parse', `refs/tags/${plan.gitTag}`), f.source);
      assert.deepEqual(JSON.parse(git(f.remote, 'notes', `--ref=semantic-release-${plan.gitTag}`, 'show', plan.gitTag)), { channels: [plan.channel] });
      assert.equal(git(f.cwd, 'status', '--porcelain'), '');
    } finally { await f.close(); }
  });
}

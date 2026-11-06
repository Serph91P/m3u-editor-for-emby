import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, rm, writeFile, readFile, mkdir, copyFile, chmod } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync, spawn } from 'node:child_process';
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
    const fail = operation === failure?.operation && (!injected || failure.always);
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


for (const operation of ['create', 'upload', 'publish']) {
  test(`permanent ${operation} failure terminates with bounded writes`, async () => {
    const f = await githubFixture({ operation, after: false, status: 500, always: true });
    try {
      const { GitHub, publishRelease } = await import('../release.mjs');
      await assert.rejects(publishRelease({ github: new GitHub('synthetic-token', f.base, f.base), plan: { gitTag: 'v1.0.1', gitHead: 'a'.repeat(40), channel: null, notes: 'notes' }, assets: new Map([['one.dll', Buffer.from('dll')], ['one.zip', Buffer.from('zip')]]) }), operation === 'publish' ? /Published release readback failed/ : /converge/);
      assert.equal(f.writes(), operation === 'create' ? 3 : operation === 'upload' ? 4 : 6);
      assert.equal(f.releases.length, operation === 'create' ? 0 : 1);
      if (f.releases.length) assert.equal(f.releases[0].draft, true);
    } finally { await f.close(); }
  });
}

for (const stop of [1, 2, 4]) {
  test(`source authority loss before write ${stop} stops publisher and remains restartable`, async () => {
    const f = await githubFixture();
    try {
      const { GitHub, publishRelease } = await import('../release.mjs');
      const github = new GitHub('synthetic-token', f.base, f.base);
      const plan = { gitTag: 'v1.0.1', channel: null, gitHead: 'a'.repeat(40), notes: 'notes' };
      const assets = new Map([['one.dll', Buffer.from('dll')], ['one.zip', Buffer.from('zip')]]);
      let checks = 0;
      await assert.rejects(publishRelease({ github, plan, assets, beforeWrite: () => { if (++checks === stop) throw new Error('source moved'); } }), /source moved/);
      assert.equal(f.writes(), stop - 1);
      await publishRelease({ github, plan, assets });
      assert.equal(f.releases.length, 1); assert.equal(f.releases[0].draft, false);
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

for (const status of [403, 500, 'malformed', 'malformed-list', 'timeout']) {
  test(`read failure ${status} cannot authorize creation`, async () => {
    let requests = 0;
    const server = createServer((req, res) => {
      requests++;
      if (req.url === '/repos/Serph91P/m3u-editor-for-emby') { res.writeHead(200); return res.end(JSON.stringify({ full_name: 'Serph91P/m3u-editor-for-emby', fork: false, permissions: { push: true } })); }
      if (status === 'timeout') return;
      res.writeHead(typeof status === 'string' ? 200 : status, { 'Content-Type': 'application/json' });
      res.end(status === 'malformed' ? '{' : status === 'malformed-list' ? '[{}]' : '{}');
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
      const { GitHub, publishRelease } = await import('../release.mjs');
      const github = new GitHub('synthetic-token', `http://127.0.0.1:${server.address().port}`);
      await assert.rejects(publishRelease({ github, plan: { gitTag: 'v1.0.1', gitHead: 'a'.repeat(40), channel: null, notes: 'notes' }, assets: new Map() }));
      assert.equal(requests, status === 500 || status === 'timeout' ? 4 : 2);
    } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
  });
}

const root = fileURLToPath(new URL('../../../', import.meta.url));
const run = (command, args, options) => new Promise((resolve, reject) => {
  const child = spawn(command, args, options); let out = '', err = '';
  child.stdout.on('data', chunk => out += chunk); child.stderr.on('data', chunk => err += chunk);
  child.stdin?.end(options?.input);
  child.on('error', reject); child.on('close', code => resolve({ code, out, err }));
});

test('ephemeral token Git authentication works without persisted credentials and never reaches another host', async () => {
  const { gitEnvironment } = await import('../release.mjs');
  assert.equal(typeof gitEnvironment, 'function');
  const dir = await mkdtemp(join(tmpdir(), 'release-auth-'));
  let authenticated = 0, leaked = 0;
  const foreign = createServer((req, res) => { if (req.headers.authorization) leaked++; res.writeHead(403); res.end(); });
  await new Promise(resolve => foreign.listen(0, '127.0.0.1', resolve));
  const server = createServer((req, res) => {
    if (req.headers.authorization === `basic ${Buffer.from('x-access-token:synthetic-token').toString('base64')}`) authenticated++;
    res.writeHead(302, { Location: `http://localhost:${foreign.address().port}${req.url}` }); res.end();
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const env = gitEnvironment({ ...process.env, GITHUB_TOKEN: 'synthetic-token' });
    // Map the canonical HTTPS transport to a disposable HTTP server, keeping the header's URL scope.
    const keys = Object.keys(env).filter(key => key.startsWith('GIT_CONFIG_KEY_') && env[key].endsWith('.extraheader'));
    assert.ok(keys.length > 0);
    for (const key of keys) env[key] = `http.http://127.0.0.1:${server.address().port}/.extraheader`;
    const result = await run('git', ['ls-remote', `http://127.0.0.1:${server.address().port}/remote.git`], { cwd: dir, env });
    assert.notEqual(result.code, 0);
    assert.ok(authenticated > 0, 'native Git must actually transmit scoped auth');
    assert.equal(leaked, 0);
    assert.equal(result.err.includes('synthetic-token'), false);
    assert.equal(await readFile(join(dir, '.gitconfig'), 'utf8').catch(() => ''), '');
  } finally { await rm(dir, { recursive: true, force: true }); await new Promise(resolve => server.close(resolve)); await new Promise(resolve => foreign.close(resolve)); }
});

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

async function seedPublishedRelease(http, f, tag, source, channel, body) {
  const { buildBundle } = await import('../release.mjs');
  const built = await buildBundle({ cwd: f.cwd, plan: { version: tag.slice(1), gitTag: tag, gitHead: source, channel } });
  const release = { id: 901, tag_name: tag, name: tag, target_commitish: f.branch, prerelease: channel !== null, body, draft: false,
    assets: [...built].map(([name, bytes], i) => ({ id: 1000 + i, name, bytes, size: bytes.length, state: 'uploaded', digest: `sha256:${createHash('sha256').update(bytes).digest('hex')}` })) };
  http.releases.push(release);
  return release;
}

for (const scenario of ['main-first', 'develop-first', 'main-fresh', 'develop-fresh', 'tag-only-old-source', 'tag-only-old-source-main-advanced', 'notes-only-old-source', 'notes-push-failure', 'tag-push-failure', 'legacy-published-noop', 'original-zip-partial']) {
  test(`real runner and builder: ${scenario}, immutable source and ${scenario === 'develop-first' ? 'mandatory stable-floor rejection' : 'verified rerun'}`, { timeout: 180000 }, async () => {
    const branch = scenario.startsWith('main') ? 'main' : 'develop';
    const f = await fixture(branch, true), http = await githubFixture({ operation: 'upload', after: true, status: 'disconnect' });
    try {
      const { GitHub, runRelease, planRelease, reconcileBundle } = await import('../release.mjs');
      assert.equal(typeof runRelease, 'function', 'real release controller is required');
      if (scenario.endsWith('-first')) {
        git(f.cwd, 'tag', '-d', 'v1.0.0'); git(f.remote, 'update-ref', '-d', 'refs/tags/v1.0.0');
      }
      if (scenario === 'develop-first') {
        redirectOrigin(f); const refs = git(f.remote, 'show-ref');
        await assert.rejects(runRelease({ cwd: f.cwd, env: runnerEnv(f), github: new GitHub('synthetic-token', http.base, http.base) }), error => error.errors?.some(cause => cause.message.includes('Stable main release metadata is unavailable')));
        assert.equal(http.writes(), 0); assert.equal(git(f.remote, 'show-ref'), refs);
        return;
      }
      const initial = await planRelease({ cwd: f.cwd, source: f.source, branch });
      if (branch === 'main' && !scenario.endsWith('-first')) {
        git(f.cwd, 'notes', '--ref=semantic-release-v1.0.0', 'add', '-m', '{"channels":[null]}', 'v1.0.0');
        git(f.cwd, 'push', 'origin', 'refs/notes/semantic-release-v1.0.0');
        await seedPublishedRelease(http, f, 'v1.0.0', f.stable, null, 'legacy stable notes');
      }
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
      if (scenario === 'tag-only-old-source-main-advanced') {
        git(f.cwd, 'checkout', 'main');
        await writeFile(join(f.cwd, 'fixture'), 'later main branch movement');
        git(f.cwd, 'commit', '-am', 'ci: later main controller'); git(f.cwd, 'push', 'origin', 'main');
        git(f.cwd, 'checkout', branch);
      }
      // Only a disposable Git remote is redirected; the real guard sees the canonical identity.
      git(f.cwd, 'config', `url.${f.remote}.insteadOf`, 'https://github.com/Serph91P/m3u-editor-for-emby.git');
      git(f.cwd, 'remote', 'set-url', 'origin', 'https://github.com/Serph91P/m3u-editor-for-emby.git');
      const env = { ...process.env, GITHUB_EVENT_NAME: 'push', GITHUB_REPOSITORY: 'Serph91P/m3u-editor-for-emby', GITHUB_REF_NAME: branch, GITHUB_REF: `refs/heads/${branch}`, GITHUB_SHA: controller, RELEASE_BUILD_RESULT: 'success', RELEASE_CODEQL_RESULT: 'success' };
      const github = new GitHub('synthetic-token', http.base, http.base);
      if (scenario === 'notes-push-failure' || scenario === 'tag-push-failure') {
        const hook = join(f.remote, 'hooks/pre-receive');
        await writeFile(hook, `#!/bin/sh\nwhile read old new ref; do\n case "$ref" in refs/${scenario.startsWith('notes') ? 'notes' : 'tags'}/*) exit 1;; esac\ndone\n`);
        execFileSync('chmod', ['+x', hook]);
        await assert.rejects(runRelease({ cwd: f.cwd, env, github }), /notes push|notes readback|tag source mismatch/);
        assert.equal(http.writes(), 0);
        if (scenario === 'notes-push-failure') assert.equal(git(f.remote, 'rev-parse', initial.gitTag), f.source);
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
      if (scenario === 'original-zip-partial') {
        const canonical = new Map(release.assets.map(asset => [asset.name, Buffer.from(asset.bytes)]));
        const legacy = join(f.dir, 'legacy-build');
        git(f.dir, 'clone', '--no-hardlinks', f.cwd, legacy);
        git(legacy, 'checkout', '--detach', f.source);
        execFileSync('bash', [join(root, 'scripts/release/inject-version.sh'), initial.version], { cwd: legacy, stdio: 'pipe' });
        execFileSync('dotnet', ['build', '-c', 'Release', 'Emby.M3uEditor.Plugin/Emby.M3uEditor.Plugin.csproj', `-p:SourceRevisionId=${f.source}`], { cwd: legacy, stdio: 'pipe' });
        const dll = release.assets.find(a => a.name.endsWith('.dll'));
        dll.bytes = await readFile(join(legacy, 'Emby.M3uEditor.Plugin/bin/Release/netstandard2.0/Emby.M3uEditor.Plugin.dll'));
        dll.size = dll.bytes.length; dll.digest = `sha256:${createHash('sha256').update(dll.bytes).digest('hex')}`;
        const zip = release.assets.find(a => a.name.endsWith('.zip'));
        zip.bytes = execFileSync('python3', ['-c', 'import io,sys,zipfile; b=io.BytesIO(); z=zipfile.ZipFile(b,"w"); z.write(sys.argv[1],"Emby.M3uEditor.Plugin.dll"); z.comment=b"original"; z.close(); sys.stdout.buffer.write(b.getvalue())', join(legacy, 'Emby.M3uEditor.Plugin/bin/Release/netstandard2.0/Emby.M3uEditor.Plugin.dll')], { maxBuffer: 20000000 });
        zip.size = zip.bytes.length;
        zip.digest = `sha256:${createHash('sha256').update(zip.bytes).digest('hex')}`;
        const sha = release.assets.find(a => a.name.endsWith('.sha256'));
        sha.bytes = Buffer.from(`${zip.digest.slice(7)}  ${zip.name}\n`); sha.size = sha.bytes.length;
        sha.digest = `sha256:${createHash('sha256').update(sha.bytes).digest('hex')}`;
        release.draft = true;
        const original = new Map(release.assets.filter(a => !a.name.endsWith('.md5')).map(a => [a.name, Buffer.from(a.bytes)]));
        release.assets = release.assets.filter(a => !a.name.endsWith('.md5'));
        await runRelease({ cwd: f.cwd, env, github });
        assert.equal(release.draft, false); assert.equal(release.assets.length, 4);
        for (const [name, bytes] of original) assert.deepEqual(release.assets.find(a => a.name === name).bytes, bytes);
        const saved = release.assets;
        for (const mismatch of ['source', 'version', 'method']) {
          const corrupt = { ...dll, bytes: Buffer.from(dll.bytes) };
          const offset = mismatch === 'source' ? corrupt.bytes.indexOf(f.source) : mismatch === 'version' ? corrupt.bytes.indexOf(initial.version) : 0x250;
          assert.ok(offset >= 0); corrupt.bytes[offset] ^= 1;
          corrupt.digest = `sha256:${createHash('sha256').update(corrupt.bytes).digest('hex')}`;
          release.assets = [corrupt]; release.draft = true;
          const writes = http.writes();
          await assert.rejects(reconcileBundle({ github, plan: initial, built: canonical, release }), /Conflicting DLL/);
          assert.equal(http.writes(), writes);
        }
        release.assets = saved; release.draft = false;
      }
      if (scenario === 'legacy-published-noop') {
        release.body = 'Original native notes with historical date, never regenerate';
        release.target_commitish = branch;
        git(f.cwd, 'notes', '--ref=semantic-release-v0.5.0', 'add', '-m', '{"channels":[null]}', f.source);
        git(f.cwd, 'push', 'origin', 'refs/notes/semantic-release-v0.5.0');
      }
      const before = JSON.stringify(http.releases), writes = http.writes();
      await runRelease({ cwd: f.cwd, env, github });
      assert.equal(http.writes(), writes);
      assert.equal(JSON.stringify(http.releases), before);
      assert.equal(git(f.cwd, 'status', '--porcelain'), '');
    } finally { await f.close(); await http.close(); }
  });
}

test('superseded legacy gap is untouched while the current orphan recovers above a published frontier', async () => {
  const f = await fixture('develop', true), http = await githubFixture();
  try {
    const { GitHub, runRelease, planRelease } = await import('../release.mjs');
    const plan = await planRelease({ cwd: f.cwd, source: f.source, branch: 'develop' });
    git(f.cwd, 'tag', 'v0.9.1-beta.1', f.stable);
    git(f.cwd, 'tag', 'v0.8.0-beta.1', f.stable);
    git(f.cwd, 'tag', plan.gitTag, f.source);
    git(f.cwd, 'tag', 'v1.0.0-beta.1', f.stable);
    git(f.cwd, 'notes', '--ref=semantic-release-v1.0.0-beta.1', 'add', '-m', '{"channels":["develop"]}', 'v1.0.0-beta.1');
    git(f.cwd, 'push', 'origin', 'refs/notes/semantic-release-v1.0.0-beta.1');
    git(f.cwd, 'push', 'origin', '--tags');
    await seedPublishedRelease(http, f, 'v1.0.0-beta.1', f.stable, 'develop', 'original legacy notes');
    const legacy = JSON.stringify(http.releases[0]);
    http.releases.push({ id: 902, tag_name: 'v0.8.0-beta.1', name: 'v0.8.0-beta.1', target_commitish: 'develop', prerelease: true, body: 'ancient notes with no recoverable channel note', draft: false });
    git(f.cwd, 'config', `url.${f.remote}.insteadOf`, 'https://github.com/Serph91P/m3u-editor-for-emby.git');
    git(f.cwd, 'remote', 'set-url', 'origin', 'https://github.com/Serph91P/m3u-editor-for-emby.git');
    const env = { ...process.env, GITHUB_EVENT_NAME: 'push', GITHUB_REPOSITORY: 'Serph91P/m3u-editor-for-emby', GITHUB_REF_NAME: 'develop', GITHUB_REF: 'refs/heads/develop', GITHUB_SHA: f.source, RELEASE_BUILD_RESULT: 'success', RELEASE_CODEQL_RESULT: 'success' };
    await runRelease({ cwd: f.cwd, env, github: new GitHub('synthetic-token', http.base, http.base) });
    assert.equal(JSON.stringify(http.releases[0]), legacy);
    assert.equal(http.releases.find(r => r.tag_name === plan.gitTag).draft, false);
    assert.equal(git(f.remote, 'rev-parse', 'v0.9.1-beta.1'), f.stable);
    assert.equal(git(f.remote, 'for-each-ref', '--format=%(refname)', 'refs/notes/semantic-release-v0.9.1-beta.1'), '');
    assert.equal(git(f.remote, 'rev-parse', plan.gitTag), f.source);
    assert.equal(http.releases.length, 3);
  } finally { await f.close(); await http.close(); }
});

test('native authenticated HTTP Git fetch and push use the ephemeral token with no stored credentials', async () => {
  const f = await fixture(); let authenticated = 0;
  git(f.remote, 'config', 'http.receivepack', 'true');
  const server = createServer(async (req, res) => {
    if (req.headers.authorization !== `basic ${Buffer.from('x-access-token:synthetic-token').toString('base64')}`) { res.writeHead(403); return res.end(); }
    authenticated++;
    const url = new URL(req.url, 'http://localhost'); const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const backend = spawn('git', ['http-backend'], { env: { ...process.env, GIT_PROJECT_ROOT: f.dir, GIT_HTTP_EXPORT_ALL: '1', PATH_INFO: url.pathname, QUERY_STRING: url.search.slice(1), REQUEST_METHOD: req.method, CONTENT_TYPE: req.headers['content-type'] || '', REMOTE_USER: 'synthetic' } });
    const output = []; backend.stdout.on('data', chunk => output.push(chunk)); backend.stderr.resume();
    backend.stdin.end(Buffer.concat(chunks));
    backend.on('close', () => {
      const bytes = Buffer.concat(output), split = bytes.indexOf('\r\n\r\n');
      if (split < 0) { res.writeHead(500); return res.end(); }
      const headers = bytes.subarray(0, split).toString().split('\r\n');
      let status = 200;
      for (const line of headers) { const colon = line.indexOf(':'); const key = line.slice(0, colon), value = line.slice(colon + 1).trim(); if (key === 'Status') status = Number(value.split(' ')[0]); else res.setHeader(key, value); }
      res.writeHead(status); res.end(bytes.subarray(split + 4));
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const { gitEnvironment } = await import('../release.mjs');
    const env = gitEnvironment({ ...process.env, GITHUB_TOKEN: 'synthetic-token' });
    for (const key of Object.keys(env).filter(key => key.startsWith('GIT_CONFIG_KEY_') && env[key].endsWith('.extraheader'))) env[key] = `http.http://127.0.0.1:${server.address().port}/.extraheader`;
    const clone = join(f.dir, 'http-clone');
    const result = await run('git', ['clone', `http://127.0.0.1:${server.address().port}/remote.git`, clone], { env });
    assert.equal(result.code, 0, result.err);
    const tag = 'v1.0.1-beta.1'; git(clone, 'tag', tag, f.source);
    const pushed = await run('git', ['push', 'origin', `refs/tags/${tag}`], { cwd: clone, env });
    assert.equal(pushed.code, 0, pushed.err); assert.equal(git(f.remote, 'rev-parse', tag), f.source);
    assert.ok(authenticated >= 4);
    assert.equal((await readFile(join(clone, '.git/config'), 'utf8')).includes('synthetic-token'), false);
  } finally { await new Promise(resolve => server.close(resolve)); await f.close(); }
});

const runnerEnv = (f, changes = {}) => ({ ...process.env, GITHUB_EVENT_NAME: 'push', GITHUB_REPOSITORY: 'Serph91P/m3u-editor-for-emby', GITHUB_REF_NAME: f.branch, GITHUB_REF: `refs/heads/${f.branch}`, GITHUB_SHA: git(f.cwd, 'rev-parse', 'HEAD'), RELEASE_BUILD_RESULT: 'success', RELEASE_CODEQL_RESULT: 'success', ...changes });
const redirectOrigin = f => {
  git(f.cwd, 'config', `url.${f.remote}.insteadOf`, 'https://github.com/Serph91P/m3u-editor-for-emby.git');
  git(f.cwd, 'remote', 'set-url', 'origin', 'https://github.com/Serph91P/m3u-editor-for-emby.git');
};
for (const [origin, trusted] of [
  ['https://github.com/Serph91P/m3u-editor-for-emby', true], // actions/checkout@v6 fetch URL
  ['https://github.com/Serph91P/m3u-editor-for-emby.git', true],
  ['git@github.com:Serph91P/m3u-editor-for-emby.git', true],
  ['ssh://git@github.com/Serph91P/m3u-editor-for-emby.git', true],
  ['https://github.com/Serph91P/other-repo', false],
  ['https://github.com/foreign/m3u-editor-for-emby', false],
  ['https://example.invalid/Serph91P/m3u-editor-for-emby', false],
  ['https://github.com.example.invalid/Serph91P/m3u-editor-for-emby', false],
  ['https://user@github.com/Serph91P/m3u-editor-for-emby', false],
  ['https://github.com@example.invalid/Serph91P/m3u-editor-for-emby', false],
  ['https://github.com/Serph91P/m3u-editor-for-emby?repo=other', false],
  ['https://github.com/Serph91P/m3u-editor-for-emby#other', false],
]) {
  test(`origin authority before API access: ${origin}`, async () => {
    const f = await fixture();
    try {
      const { runRelease } = await import('../release.mjs');
      // Keep the real context guard's Git fetch local even for rejected origins.
      git(f.cwd, 'config', `url.${f.remote}.insteadOf`, origin);
      git(f.cwd, 'remote', 'set-url', 'origin', origin);
      const remoteRefs = git(f.remote, 'show-ref'), localRefs = git(f.cwd, 'show-ref');
      const boundary = new Error('stop before API access');
      let authentications = 0;
      await assert.rejects(runRelease({ cwd: f.cwd, env: runnerEnv(f), github: {
        authenticate: async () => { authentications++; throw boundary; },
      } }), trusted ? error => error === boundary : /Untrusted origin repository/);
      assert.equal(authentications, trusted ? 1 : 0);
      assert.equal(git(f.remote, 'show-ref'), remoteRefs, 'no remote tag/notes/branch writes');
      assert.equal(git(f.cwd, 'show-ref'), localRefs, 'no local tag/notes/branch writes');
    } finally { await f.close(); }
  });
}

for (const scenario of ['omitted', 'false', 'omitted-readonly', 'false-readonly', 'not-installation', 'false-not-installation', 'foreign', 'fork', 'invalid-token']) {
  test(`installation token authority before writes: ${scenario}`, async () => {
    const f = await fixture();
    const authority = { full_name: 'Serph91P/m3u-editor-for-emby', fork: false, clone_url: 'https://github.com/Serph91P/m3u-editor-for-emby.git' };
    if (scenario.startsWith('false')) authority.permissions = { push: false };
    if (scenario === 'foreign') authority.full_name = 'foreign/repo';
    if (scenario === 'fork') authority.fork = true;
    let writes = 0, gitRequests = 0, installationChecks = 0;
    git(f.remote, 'config', 'http.receivepack', 'true');
    const server = createServer(async (req, res) => {
      const url = new URL(req.url, 'http://localhost');
      if (!['GET', 'HEAD'].includes(req.method)) writes++;
      if (url.pathname.startsWith('/remote.git/')) {
        gitRequests++;
        if (scenario.endsWith('readonly') || req.headers.authorization !== `basic ${Buffer.from('x-access-token:synthetic-token').toString('base64')}`) { res.writeHead(403); return res.end(); }
        const backend = spawn('git', ['http-backend'], { env: { ...process.env, GIT_PROJECT_ROOT: f.dir, GIT_HTTP_EXPORT_ALL: '1', PATH_INFO: url.pathname, QUERY_STRING: url.search.slice(1), REQUEST_METHOD: req.method, REMOTE_USER: 'synthetic' } });
        const output = []; backend.stdout.on('data', chunk => output.push(chunk)); backend.stderr.resume(); backend.stdin.end();
        backend.on('close', () => {
          const bytes = Buffer.concat(output), split = bytes.indexOf('\r\n\r\n');
          if (split < 0) { res.writeHead(500); return res.end(); }
          for (const line of bytes.subarray(0, split).toString().split('\r\n')) { const colon = line.indexOf(':'); res.setHeader(line.slice(0, colon), line.slice(colon + 1).trim()); }
          res.writeHead(200); res.end(bytes.subarray(split + 4));
        });
        return;
      }
      if (req.headers.authorization !== 'Bearer synthetic-token' || scenario === 'invalid-token') { res.writeHead(401); return res.end('{}'); }
      if (url.pathname === '/installation/repositories' && req.method === 'HEAD') {
        installationChecks++; res.writeHead(scenario.endsWith('not-installation') ? 403 : 200); return res.end();
      }
      res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(authority));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
      const { GitHub, runRelease, publishRelease } = await import('../release.mjs');
      const base = `http://127.0.0.1:${server.address().port}`;
      git(f.cwd, 'remote', 'set-url', 'origin', 'https://github.com/Serph91P/m3u-editor-for-emby');
      git(f.cwd, 'config', `url.${f.remote}.insteadOf`, 'https://github.com/Serph91P/m3u-editor-for-emby');
      git(f.cwd, 'config', `url.${base}/remote.git.insteadOf`, 'https://github.com/Serph91P/m3u-editor-for-emby.git');
      git(f.cwd, 'config', `http.${base}/.extraheader`, 'AUTHORIZATION: wrong-persisted-token');
      // Translate only the scoped auth header to the isolated HTTP transport; run real Git unchanged.
      const bin = join(f.dir, 'bin'), calls = join(f.dir, 'git-calls'); await mkdir(bin);
      const realGit = execFileSync('which', ['git'], { encoding: 'utf8' }).trim();
      await writeFile(join(bin, 'git'), `#!/usr/bin/env python3\nimport os,sys,json\nenv=dict(os.environ)\nfor key in env:\n if key.startswith('GIT_CONFIG_KEY_') and env[key]=='http.https://github.com/.extraheader': env[key]=${JSON.stringify(`http.${base}/.extraheader`)}\nif 'push' in sys.argv: open(${JSON.stringify(calls)},'a').write(json.dumps(sys.argv[1:])+'\\n')\nos.execve(${JSON.stringify(realGit)},[${JSON.stringify(realGit)}]+sys.argv[1:],env)\n`);
      await chmod(join(bin, 'git'), 0o755);
      const env = runnerEnv(f, { PATH: `${bin}:${process.env.PATH}`, GITHUB_TOKEN: 'different-env-token' });
      const remoteRefs = git(f.remote, 'show-ref'), localRefs = git(f.cwd, 'show-ref');
      const github = new GitHub('synthetic-token', base, base);
      const boundary = new Error('stop after authenticated authority');
      github.list = async () => { throw boundary; };
      const trusted = ['omitted', 'false'].includes(scenario);
      const rejection = scenario.endsWith('readonly') ? /Cannot prove installation token repository push authority/ : scenario.endsWith('not-installation') ? /GitHub HEAD .*HTTP 403/ : scenario === 'invalid-token' ? /GitHub GET .*HTTP 401/ : /Cannot prove repository identity/;
      await assert.rejects(runRelease({ cwd: f.cwd, env, github }), trusted ? error => error === boundary : rejection);
      if (trusted) await assert.rejects(publishRelease({ cwd: f.cwd, env, github, plan: { gitTag: 'v1.0.1-beta.1' } }), error => error === boundary);
      const pushes = (await readFile(calls, 'utf8').catch(() => '')).trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
      if (trusted || scenario.endsWith('readonly')) {
        assert.deepEqual(pushes, Array.from({ length: trusted ? 2 : 1 }, () => ['push', '--dry-run', '--no-verify', '--', 'https://github.com/Serph91P/m3u-editor-for-emby.git', 'HEAD:refs/heads/develop']));
        assert.equal(installationChecks, trusted ? 2 : 1); assert.ok(gitRequests > 0);
      } else assert.deepEqual(pushes, []);
      assert.equal(writes, 0, 'neither REST writes nor Git receive-pack POSTs');
      assert.equal(git(f.remote, 'show-ref'), remoteRefs);
      assert.equal(git(f.cwd, 'show-ref'), localRefs);
    } finally { await new Promise(resolve => server.close(resolve)); await f.close(); }
  });
}

const trustedRun = f => ({ id: 100, workflow_id: 42, path: '.github/workflows/ci.yml', head_sha: f.source, head_branch: f.branch, event: 'push', head_repository: { full_name: 'Serph91P/m3u-editor-for-emby', fork: false }, status: 'completed', conclusion: 'failure', jobs: [
  { name: 'Build & Test', conclusion: 'success', head_sha: f.source },
  { name: 'CodeQL Security Analysis', conclusion: 'success', head_sha: f.source },
] });

for (const mutation of ['fork', 'repository', 'pr', 'sha', 'branch', 'workflow', 'path', 'pending', 'build', 'codeql', 'job-sha', 'duplicate-job', 'no-runs']) {
  test(`historical ${mutation} CI evidence is rejected before any remote mutation`, async () => {
    const f = await fixture(), http = await githubFixture();
    try {
      const { GitHub, runRelease, planRelease } = await import('../release.mjs');
      const plan = await planRelease({ cwd: f.cwd, source: f.source, branch: f.branch });
      git(f.cwd, 'tag', plan.gitTag, f.source); git(f.cwd, 'push', 'origin', '--tags');
      await writeFile(join(f.cwd, 'fixture'), 'controller'); git(f.cwd, 'commit', '-am', 'ci: controller'); git(f.cwd, 'push', 'origin', f.branch);
      const run = trustedRun(f);
      if (mutation === 'fork') run.head_repository.fork = true;
      if (mutation === 'repository') run.head_repository.full_name = 'foreign/repo';
      if (mutation === 'pr') run.event = 'pull_request';
      if (mutation === 'sha') run.head_sha = 'b'.repeat(40);
      if (mutation === 'branch') run.head_branch = 'main';
      if (mutation === 'workflow') run.workflow_id = 43;
      if (mutation === 'path') run.path = '.github/workflows/other.yml';
      if (mutation === 'pending') run.status = 'in_progress';
      if (mutation === 'build') run.jobs[0].conclusion = 'failure';
      if (mutation === 'codeql') run.jobs[1].conclusion = 'skipped';
      if (mutation === 'job-sha') run.jobs[1].head_sha = 'b'.repeat(40);
      if (mutation === 'duplicate-job') run.jobs.push({ ...run.jobs[0] });
      if (mutation !== 'no-runs') http.runs.push(run);
      redirectOrigin(f);
      const refs = git(f.remote, 'show-ref');
      await assert.rejects(runRelease({ cwd: f.cwd, env: runnerEnv(f), github: new GitHub('synthetic-token', http.base, http.base) }), /No trusted push CI/);
      assert.equal(http.writes(), 0); assert.equal(git(f.remote, 'show-ref'), refs);
    } finally { await f.close(); await http.close(); }
  });
}

for (const mutation of ['pr', 'fork', 'controller-sha', 'source-moved', 'origin', 'build', 'codeql', 'multiple-above-frontier', 'multiple-no-frontier', 'notes-channel', 'tag-version', 'release-notes', 'release-sha']) {
  test(`real controller rejects ${mutation} with zero writes`, async () => {
    const f = await fixture('develop', mutation.startsWith('release-') || mutation === 'multiple-above-frontier'), http = await githubFixture();
    try {
      const { GitHub, runRelease, planRelease } = await import('../release.mjs');
      const plan = await planRelease({ cwd: f.cwd, source: f.source, branch: f.branch });
      if (mutation.startsWith('release-')) http.releases.push({ id: 901, tag_name: plan.gitTag, name: plan.gitTag, target_commitish: mutation === 'release-sha' ? 'b'.repeat(40) : f.source, prerelease: true, body: mutation === 'release-notes' ? 'foreign notes' : plan.notes, draft: true, assets: [] });
      git(f.cwd, 'tag', plan.gitTag, f.source);
      if (mutation.startsWith('multiple')) git(f.cwd, 'tag', 'v0.9.1-beta.1', f.stable);
      if (mutation === 'multiple-above-frontier') {
        git(f.cwd, 'tag', 'v0.8.0-beta.1', f.stable);
        git(f.cwd, 'notes', '--ref=semantic-release-v0.8.0-beta.1', 'add', '-m', '{"channels":["develop"]}', f.stable);
        git(f.cwd, 'push', 'origin', 'refs/notes/semantic-release-v0.8.0-beta.1');
        await seedPublishedRelease(http, f, 'v0.8.0-beta.1', f.stable, 'develop', 'old');
      }
      if (mutation === 'notes-channel') {
        git(f.cwd, 'notes', `--ref=semantic-release-${plan.gitTag}`, 'add', '-m', '{"channels":["beta"]}', plan.gitTag);
        git(f.cwd, 'push', 'origin', `refs/notes/semantic-release-${plan.gitTag}`);
      }
      if (mutation === 'tag-version') { git(f.cwd, 'tag', '-d', plan.gitTag); git(f.cwd, 'tag', 'v9.0.0-beta.1', f.source); }
      git(f.cwd, 'push', 'origin', '--tags');
      const env = runnerEnv(f);
      if (mutation === 'pr') env.GITHUB_EVENT_NAME = 'pull_request';
      if (mutation === 'fork') env.GITHUB_REPOSITORY = 'foreign/repo';
      if (mutation === 'controller-sha') env.GITHUB_SHA = f.stable;
      if (mutation === 'source-moved') git(f.remote, 'update-ref', `refs/heads/${f.branch}`, f.stable);
      if (mutation === 'build') env.RELEASE_BUILD_RESULT = 'failure';
      if (mutation === 'codeql') env.RELEASE_CODEQL_RESULT = 'pending';
      redirectOrigin(f);
      if (mutation === 'origin') git(f.cwd, 'config', 'remote.origin.url', f.remote);
      const refs = git(f.remote, 'show-ref');
      await assert.rejects(runRelease({ cwd: f.cwd, env, github: new GitHub('synthetic-token', http.base, http.base) }), mutation.startsWith('multiple') ? /Multiple unresolved/ : undefined);
      assert.equal(http.writes(), 0); assert.equal(git(f.remote, 'show-ref'), refs);
    } finally { await f.close(); await http.close(); }
  });
}

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

for (const branch of ['main', 'develop']) {
  test(`published frontier ${branch} blocks missing/conflicting assets before a newer release`, { timeout: 180000 }, async t => {
    const f = await fixture(branch, true), http = await githubFixture();
    try {
      const { GitHub, runRelease } = await import('../release.mjs');
      if (branch === 'main') {
        git(f.cwd, 'tag', '-d', 'v1.0.0'); git(f.remote, 'update-ref', '-d', 'refs/tags/v1.0.0');
      }
      redirectOrigin(f);
      const github = new GitHub('synthetic-token', http.base, http.base);
      await runRelease({ cwd: f.cwd, env: runnerEnv(f), github });
      const release = http.releases[0];
      const complete = release.assets.map(asset => ({ ...asset, bytes: Buffer.from(asset.bytes) }));
      await writeFile(join(f.cwd, 'fixture'), 'new releasable change');
      git(f.cwd, 'commit', '-am', 'fix: next synthetic product change');
      git(f.cwd, 'push', 'origin', f.branch);
      const refs = git(f.remote, 'show-ref'), writes = http.writes();
      for (const mutation of ['missing-md5', 'conflicting-md5']) {
        await t.test(mutation, async () => {
          release.assets = complete.map(asset => ({ ...asset, bytes: Buffer.from(asset.bytes) }));
          if (mutation === 'missing-md5') release.assets = release.assets.filter(asset => !asset.name.endsWith('.md5'));
          else {
            const asset = release.assets.find(asset => asset.name.endsWith('.md5'));
            asset.bytes[0] ^= 1;
            asset.digest = `sha256:${createHash('sha256').update(asset.bytes).digest('hex')}`;
          }
          const original = JSON.stringify(http.releases);
          await assert.rejects(runRelease({ cwd: f.cwd, env: runnerEnv(f), github }), /incomplete|Conflicting md5/);
          assert.equal(http.writes(), writes, 'must not repair published assets or publish a successor');
          assert.equal(git(f.remote, 'show-ref'), refs, 'must not push successor tag or notes');
          assert.equal(JSON.stringify(http.releases), original);
        });
      }
      // Restore only the synthetic fixture, not through the publisher; a valid frontier must permit progress.
      release.assets = complete;
      const frontier = JSON.stringify(release);
      const result = await runRelease({ cwd: f.cwd, env: runnerEnv(f), github });
      assert.notEqual(result.plan.gitTag, release.tag_name);
      assert.equal(http.releases.length, 2);
      assert.equal(result.release.draft, false);
      assert.equal(JSON.stringify(release), frontier);
    } finally { await f.close(); await http.close(); }
  });
}

test('original DLL with incompatible PE/CLR headers is rejected with a real loader negative control', { timeout: 180000 }, async t => {
  const f = await fixture('develop', true), http = await githubFixture();
  try {
    const { GitHub, buildBundle, planRelease, reconcileBundle } = await import('../release.mjs');
    const github = new GitHub('synthetic-token', http.base, http.base);
    const plan = await planRelease({ cwd: f.cwd, source: f.source, branch: f.branch });
    const built = await buildBundle({ cwd: f.cwd, plan });
    const name = 'Emby.M3uEditor.Plugin.dll', bytes = Buffer.from(built.get(name));
    const pe = bytes.readUInt32LE(0x3c);
    assert.equal(bytes.readUInt16LE(pe + 4), 0x014c);
    bytes.writeUInt16LE(0xaa64, pe + 4);
    const release = { id: 999, draft: true, assets: [{ id: 998, name, state: 'uploaded', bytes, size: bytes.length, digest: `sha256:${createHash('sha256').update(bytes).digest('hex')}` }] };
    http.releases.push(release);
    const loader = join(f.dir, 'loader'); await mkdir(loader);
    await writeFile(join(loader, 'loader.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>');
    await writeFile(join(loader, 'Program.cs'), 'using System; using System.Runtime.Loader; class Probe { static int Main(string[] a) { try { Console.WriteLine(AssemblyLoadContext.Default.LoadFromAssemblyPath(a[0]).FullName); return 0; } catch(Exception e) { Console.WriteLine(e.GetType().Name); return 1; } } }');
    const good = join(f.dir, 'good.dll'), bad = join(f.dir, 'bad.dll');
    await writeFile(good, built.get(name)); await writeFile(bad, bytes);
    const runLoader = path => run('dotnet', ['run', '--project', join(loader, 'loader.csproj'), '--configuration', 'Release', '--', path]);
    const control = await runLoader(good), broken = await runLoader(bad);
    assert.equal(control.code, 0, control.out + control.err);
    assert.equal(broken.code, 1, broken.out + broken.err);
    assert.match(broken.out, /BadImageFormatException/);
    const optional = pe + 24, sections = optional + bytes.readUInt16LE(pe + 20);
    const corRva = bytes.readUInt32LE(optional + 96 + 14 * 8);
    let cor;
    for (let i = 0; i < bytes.readUInt16LE(pe + 6); i++) {
      const section = sections + i * 40, rva = bytes.readUInt32LE(section + 12);
      if (corRva >= rva && corRva < rva + bytes.readUInt32LE(section + 8)) cor = bytes.readUInt32LE(section + 20) + corRva - rva;
    }
    assert.ok(cor);
    for (const [mutation, offset] of [['machine', pe + 4], ['coff-characteristics', pe + 22], ['subsystem', optional + 68], ['dll-characteristics', optional + 70], ['alignment', optional + 32], ['runtime-version', cor + 4], ['entrypoint', cor + 20], ['native-header', cor + 64], ['section-permissions', sections + 36], ['image-size', optional + 56], ['header-size', optional + 60], ['native-entrypoint', optional + 16], ['tls-directory', optional + 96 + 9 * 8]]) {
      await t.test(mutation, async () => {
        const asset = release.assets[0];
        asset.bytes = Buffer.from(built.get(name));
        asset.bytes[offset] ^= 1;
        if (mutation === 'machine') asset.bytes.writeUInt16LE(0xaa64, offset);
        asset.digest = `sha256:${createHash('sha256').update(asset.bytes).digest('hex')}`;
        const original = JSON.stringify(release);
        await assert.rejects(reconcileBundle({ github, plan, built, release }), /Conflicting DLL/);
        assert.equal(http.writes(), 0);
        assert.equal(JSON.stringify(release), original);
      });
    }
  } finally { await f.close(); await http.close(); }
});

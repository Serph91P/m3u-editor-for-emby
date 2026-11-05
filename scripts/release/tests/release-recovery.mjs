import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, rm, writeFile, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const git = (cwd, ...args) => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
async function fixture(branch = 'develop') {
  const dir = await mkdtemp(join(tmpdir(), 'release-recovery-'));
  const cwd = join(dir, 'source');
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
  const remote = join(dir, 'remote.git');
  git(dir, 'clone', '--bare', cwd, remote);
  git(cwd, 'remote', 'add', 'origin', remote);
  git(cwd, 'fetch', 'origin', '+refs/heads/*:refs/remotes/origin/*');
  return { dir, cwd, source, remote, stable, branch, close: () => rm(dir, { recursive: true, force: true }) };
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

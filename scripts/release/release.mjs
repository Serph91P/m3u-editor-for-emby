import { execFileSync } from 'node:child_process';
import { mkdtemp, rm, symlink, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Writable } from 'node:stream';
import semanticRelease from 'semantic-release';

const root = fileURLToPath(new URL('../../', import.meta.url));
const repository = 'Serph91P/m3u-editor-for-emby';
const repositoryUrl = `https://github.com/${repository}.git`;
const gitEnv = { ...process.env, GIT_TERMINAL_PROMPT: '0', GIT_AUTHOR_NAME: 'release', GIT_AUTHOR_EMAIL: 'release@example.invalid', GIT_COMMITTER_NAME: 'release', GIT_COMMITTER_EMAIL: 'release@example.invalid' };
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: gitEnv, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const ancestor = (cwd, a, b) => { try { git(cwd, 'merge-base', '--is-ancestor', a, b); return true; } catch (error) { if (error.status === 1) return false; throw error; } };
const config = JSON.parse(await readFile(join(root, '.releaserc.json'), 'utf8'));

// Only this disposable snapshot hides a candidate tag; origin and CI identity stay untouched.
export async function planRelease({ cwd, source, branch, excludeTag }) {
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
    for (const ref of git(cwd, 'for-each-ref', '--format=%(refname)', 'refs/notes/semantic-release').split('\n').filter(Boolean)) {
      git(remote, 'fetch', cwd, `${ref}:${ref}`);
    }
    git(dir, 'clone', '--branch', branch, remote, snapshot);
    git(snapshot, 'config', `url.${remote}.insteadOf`, repositoryUrl);
    git(snapshot, 'remote', 'set-url', 'origin', repositoryUrl);
    await symlink(join(root, 'node_modules'), join(snapshot, 'node_modules'), 'dir');
    const plugins = config.plugins.filter(([name]) => !['@semantic-release/exec', '@semantic-release/github'].includes(name))
      .map(([name, options]) => [name.startsWith('./') ? join(root, name) : name, options || {}]);
    const sink = new Writable({ write(chunk, encoding, done) { done(); } });
    // Explicit non-CI environment belongs only to a local, non-publishing planner.
    const env = { PATH: process.env.PATH, HOME: process.env.HOME, TMPDIR: process.env.TMPDIR, ...Object.fromEntries(Object.entries(gitEnv).filter(([key]) => key.startsWith('GIT_'))) };
    const result = await semanticRelease({ dryRun: true, repositoryUrl, branches: config.branches, tagFormat: config.tagFormat, plugins }, { cwd: snapshot, env, stdout: sink, stderr: sink });
    return result?.nextRelease || null;
  } finally { await rm(dir, { recursive: true, force: true }); }
}

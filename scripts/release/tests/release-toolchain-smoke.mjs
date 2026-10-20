import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';

// Exercise real release packages without invoking any publish or prepare hooks.
const root = fileURLToPath(new URL('../../../', import.meta.url));
const require = createRequire(`${root}/package.json`);
const load = async name => import(pathToFileURL(require.resolve(name)).href);
const config = JSON.parse(readFileSync(`${root}/.releaserc.json`, 'utf8'));
const analyzerConfig = config.plugins.find(p => p[0] === '@semantic-release/commit-analyzer')[1];
const { analyzeCommits } = await load('@semantic-release/commit-analyzer');
const logger = { log() {}, error() {}, success() {} };
for (const [message, expected] of [
  ['fix: resolve regression', 'patch'],
  ['feat: add synthetic feature', 'minor'],
  ['feat!: change synthetic API\n\nBREAKING CHANGE: synthetic API removed', 'major'],
  ['chore: refresh dependencies', 'patch'],
  ['ci: adjust synthetic workflow', null],
]) {
  assert.equal(await analyzeCommits(analyzerConfig, {
    cwd: root, logger, commits: [{ message, hash: 'a'.repeat(40) }],
  }), expected);
}
const { default: micromatch } = await load('micromatch');
assert.deepEqual(micromatch(['main', 'develop', 'fix/synthetic'], 'main'), ['main']);
assert.deepEqual(micromatch(['main', 'develop', 'fix/synthetic'], 'develop'), ['develop']);
const { generateNotes } = await load('@semantic-release/release-notes-generator');
const notes = await generateNotes({ preset: 'conventionalcommits' }, {
  cwd: root, logger,
  options: { repositoryUrl: 'https://github.com/Serph91P/m3u-editor-for-emby.git' },
  commits: [{ message: 'fix: synthetic release smoke', hash: 'b'.repeat(40) }],
  lastRelease: { version: '1.6.0', gitTag: 'v1.6.0', gitHead: 'c'.repeat(40) },
  nextRelease: { version: '1.6.1', gitTag: 'v1.6.1', gitHead: 'd'.repeat(40) },
});
assert.match(notes, /synthetic release smoke/);
assert.match(notes, /1\.6\.1/);
for (const name of ['semantic-release', '@semantic-release/exec', '@semantic-release/github']) {
  assert.ok(await load(name));
}
console.log('PASS: commit classifications, branch matching, real release notes and plugin imports; no publish hooks invoked.');

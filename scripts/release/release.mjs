import { execFile, execFileSync } from 'node:child_process';
import { mkdtemp, rm, symlink, readFile, readdir, writeFile } from 'node:fs/promises';
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
      if (path === `${repoPath}/releases`) requireState(items.every(release => release && Number.isSafeInteger(release.id) && release.id > 0 && typeof release.tag_name === 'string' && typeof release.draft === 'boolean' && typeof release.prerelease === 'boolean' && typeof release.body === 'string' && typeof release.target_commitish === 'string'), 'Malformed GitHub release list');
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
  async authenticate({ cwd, env } = {}) {
    const { data } = await this.request(repoPath);
    requireState(data.full_name === repository && data.fork === false, 'Cannot prove repository identity');
    if (data.permissions?.push === true) return;
    requireState(cwd && env?.GITHUB_REPOSITORY === repository && ['main', 'develop'].includes(env.GITHUB_REF_NAME) &&
      git(cwd, 'rev-parse', 'HEAD') === env.GITHUB_SHA, 'Cannot prove installation token controller context');
    // App tokens omit permissions or report push=false; prove token type and actual Git write access.
    await this.request('/installation/repositories?per_page=1', { method: 'HEAD', binary: true });
    await new Promise((resolve, reject) => execFile('git', ['push', '--dry-run', '--no-verify', '--', repositoryUrl, `HEAD:refs/heads/${env.GITHUB_REF_NAME}`],
      { cwd, env: gitEnvironment({ ...env, GITHUB_TOKEN: this.token }) },
      error => error ? reject(new Error('Cannot prove installation token repository push authority')) : resolve()));
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

export async function publishRelease({ github, plan, assets, publishedIdentity, beforeWrite, cwd, env }) {
  await github.authenticate({ cwd, env });
  const expected = { tag_name: plan.gitTag, target_commitish: publishedIdentity?.target_commitish ?? plan.gitHead, name: plan.gitTag, body: publishedIdentity?.body ?? plan.notes, prerelease: plan.channel != null };
  const find = async () => {
    const matches = (await github.list(`${repoPath}/releases`)).filter(release => release.tag_name === plan.gitTag);
    requireState(matches.length <= 1, `Duplicate releases for ${plan.gitTag}`);
    const release = matches[0];
    if (release) {
      requireState(Number.isSafeInteger(release.id) && typeof release.draft === 'boolean', 'Malformed release identity');
      requireState(!publishedIdentity || release.draft === false, 'Published release changed state; refusing mutation');
      for (const [key, value] of Object.entries(expected)) requireState(release[key] === value, `Conflicting release ${key} for ${plan.gitTag}`);
    }
    return release;
  };
  let release;
  for (let attempt = 0; attempt < 3; attempt++) {
    release = await find();
    if (release) break;
    await beforeWrite?.();
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
      await beforeWrite?.();
      await uncertainWrite(github, `${repoPath}/releases/${release.id}/assets?name=${encodeURIComponent(name)}`, { method: 'POST', body: assets.get(name), upload: true });
    }
    requireState(!(await verify()).includes(name), `Asset upload did not converge: ${name}`);
  }
  requireState(!(await verify()).length, 'Incomplete release asset readback');
  for (let attempt = 0; attempt < 3; attempt++) {
    release = await find();
    if (!release.draft) break;
    await beforeWrite?.();
    await uncertainWrite(github, `${repoPath}/releases/${release.id}`, { method: 'PATCH', body: { draft: false } });
  }
  release = await find();
  requireState(release && !release.draft && !(await verify()).length, 'Published release readback failed');
  return release;
}
const repository = 'Serph91P/m3u-editor-for-emby';
const repositoryUrl = `https://github.com/${repository}.git`;
export function gitEnvironment(env) {
  const result = { ...env, GIT_TERMINAL_PROMPT: '0', GIT_AUTHOR_NAME: 'release', GIT_AUTHOR_EMAIL: 'release@example.invalid', GIT_COMMITTER_NAME: 'release', GIT_COMMITTER_EMAIL: 'release@example.invalid' };
  if (env.GITHUB_TOKEN) {
    const settings = [
      ['credential.helper', ''],
      ['http.followRedirects', 'false'],
      ['http.https://github.com/.extraheader', ''],
      ['http.https://github.com/.extraheader', `AUTHORIZATION: basic ${Buffer.from(`x-access-token:${env.GITHUB_TOKEN}`).toString('base64')}`],
    ];
    let count = Number(env.GIT_CONFIG_COUNT || 0);
    for (const [key, value] of settings) { result[`GIT_CONFIG_KEY_${count}`] = key; result[`GIT_CONFIG_VALUE_${count++}`] = value; }
    result.GIT_CONFIG_COUNT = String(count);
  }
  return result;
}
const gitEnv = gitEnvironment(process.env);
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: gitEnv, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const ancestor = (cwd, a, b) => { try { git(cwd, 'merge-base', '--is-ancestor', a, b); return true; } catch (error) { if (error.status === 1) return false; throw error; } };
const config = JSON.parse(await readFile(join(root, '.releaserc.json'), 'utf8'));
const semver = createRequire(import.meta.url)(createRequire(import.meta.url).resolve('semver', { paths: [fileURLToPath(new URL('.', import.meta.resolve('semantic-release')))] }));

const sourceConfig = (cwd, source) => JSON.parse(git(cwd, 'show', `${source}:.releaserc.json`));
const notesRefs = cwd => git(cwd, 'for-each-ref', '--format=%(refname)', 'refs/notes').split('\n').filter(ref => /^refs\/notes\/semantic-release(?:-|$)/.test(ref));

function checkNotes(cwd, plan) {
  let found = false;
  for (const ref of notesRefs(cwd).filter(ref => ref === `refs/notes/semantic-release-${plan.gitTag}` || ref === 'refs/notes/semantic-release')) {
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
    requireState(git(dir, 'rev-parse', `${plan.gitTag}^{commit}`) === plan.gitHead, 'Remote tag source mismatch');
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

// Compare managed identity, metadata, every method body, static data and resources, not compiler/PDB paths.
// No assembly is loaded/executed; unsupported native payloads fail closed.
async function verifyOriginalDll(original, rebuilt) {
  if (original.equals(rebuilt)) return;
  const dir = await mkdtemp(join(tmpdir(), 'release-dll-'));
  try {
    await writeFile(join(dir, 'verify.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>');
    await writeFile(join(dir, 'Program.cs'), `
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
class Verify {
  static byte[] Identity(string path) {
    using var pe = new PEReader(File.OpenRead(path));
    if (!pe.HasMetadata || pe.PEHeaders.CorHeader.Flags != CorFlags.ILOnly) throw new Exception("Unsupported DLL");
    var md = pe.GetMetadataReader();
    using var output = new MemoryStream();
    var headers = pe.PEHeaders; var coff = headers.CoffHeader; var image = headers.PEHeader; var cor = headers.CorHeader;
    if (image == null || new[] { cor.StrongNameSignatureDirectory, cor.CodeManagerTableDirectory,
        cor.VtableFixupsDirectory, cor.ExportAddressTableJumpsDirectory, cor.ManagedNativeHeaderDirectory }
        .Any(d => d.RelativeVirtualAddress != 0 || d.Size != 0)) throw new Exception("Unsupported CLR payload");
    if (new[] { image.ExportTableDirectory, image.ExceptionTableDirectory, image.CertificateTableDirectory,
        image.CopyrightTableDirectory, image.GlobalPointerTableDirectory, image.ThreadLocalStorageTableDirectory,
        image.LoadConfigTableDirectory, image.BoundImportTableDirectory, image.DelayImportTableDirectory }
        .Any(d => d.RelativeVirtualAddress != 0 || d.Size != 0)) throw new Exception("Unsupported PE payload");
    var end = headers.SectionHeaders.Max(s => checked(s.VirtualAddress + s.VirtualSize));
    if (image.SectionAlignment <= 0 || image.SizeOfImage != checked((end + image.SectionAlignment - 1) / image.SectionAlignment * image.SectionAlignment))
      throw new Exception("Invalid image layout");
    // Supported Roslyn AnyCPU DLLs have one x86 jump through the CLR import address table.
    var iat = image.ImportAddressTableDirectory; var imports = image.ImportTableDirectory;
    if (coff.Machine != Machine.I386 || image.Magic != PEMagic.PE32 || iat.Size != 8 || imports.Size != 79)
      throw new Exception("Unsupported PE startup");
    var stub = pe.GetSectionData(image.AddressOfEntryPoint).GetContent(0, 6).ToArray();
    if (stub[0] != 0xff || stub[1] != 0x25 || BitConverter.ToUInt32(stub, 2) != checked(image.ImageBase + (uint)iat.RelativeVirtualAddress))
      throw new Exception("Invalid PE entrypoint");
    var importBytes = pe.GetSectionData(imports.RelativeVirtualAddress).GetContent(0, imports.Size).ToArray();
    foreach (var offset in new[] { 0, 12, 40 })
      BitConverter.GetBytes(checked(BitConverter.ToInt32(importBytes, offset) - imports.RelativeVirtualAddress)).CopyTo(importBytes, offset);
    output.Write(importBytes);
    var iatBytes = pe.GetSectionData(iat.RelativeVirtualAddress).GetContent(0, iat.Size).ToArray();
    BitConverter.GetBytes(checked(BitConverter.ToInt32(iatBytes, 0) - imports.RelativeVirtualAddress)).CopyTo(iatBytes, 0);
    output.Write(iatBytes);
    var reloc = image.BaseRelocationTableDirectory;
    if (reloc.Size != 12) throw new Exception("Unsupported PE relocations");
    var relocation = pe.GetSectionData(reloc.RelativeVirtualAddress).GetContent(0, reloc.Size).ToArray();
    var operand = checked(image.AddressOfEntryPoint + 2);
    if (BitConverter.ToInt32(relocation, 0) != (operand & ~0xfff) || BitConverter.ToInt32(relocation, 4) != 12 ||
        BitConverter.ToUInt16(relocation, 8) != (0x3000 | (operand & 0xfff)) || BitConverter.ToUInt16(relocation, 10) != 0)
      throw new Exception("Invalid PE relocations");
    // Compare loader identity; only compiler timestamps and debug-driven sizes/RVAs may differ.
    output.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {
      coff.Machine, coff.Characteristics, coff.SizeOfOptionalHeader, coff.NumberOfSections,
      coff.PointerToSymbolTable, coff.NumberOfSymbols,
      image.Magic, image.MajorLinkerVersion, image.MinorLinkerVersion, image.ImageBase,
      image.SectionAlignment, image.FileAlignment, image.SizeOfHeaders, image.BaseOfCode, image.BaseOfData,
      image.SizeOfUninitializedData, image.MajorOperatingSystemVersion, image.MinorOperatingSystemVersion,
      image.MajorImageVersion, image.MinorImageVersion, image.MajorSubsystemVersion, image.MinorSubsystemVersion,
      image.Subsystem, image.DllCharacteristics, image.SizeOfStackReserve, image.SizeOfStackCommit,
      image.SizeOfHeapReserve, image.SizeOfHeapCommit, image.NumberOfRvaAndSizes,
      cor.MajorRuntimeVersion, cor.MinorRuntimeVersion, cor.Flags, cor.EntryPointTokenOrRelativeVirtualAddress,
      Sections = headers.SectionHeaders.Select(s => new { s.Name, s.SectionCharacteristics })
    }));
    var metadata = pe.GetMetadata().GetContent().ToArray();
    var mvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToByteArray();
    var offsets = Enumerable.Range(0, metadata.Length - 15).Where(i => metadata.AsSpan(i, 16).SequenceEqual(mvid)).ToArray();
    if (offsets.Length != 1) throw new Exception("Ambiguous MVID");
    Array.Clear(metadata, offsets[0], 16);
    for (var row = 0; row < md.GetTableRowCount(TableIndex.FieldRva); row++)
      Array.Clear(metadata, md.GetTableMetadataOffset(TableIndex.FieldRva) + row * md.GetTableRowSize(TableIndex.FieldRva), 4);
    output.Write(metadata);
    foreach (var handle in md.MethodDefinitions) {
      var method = md.GetMethodDefinition(handle);
      if (method.RelativeVirtualAddress != 0) {
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        output.Write(pe.GetSectionData(method.RelativeVirtualAddress).GetContent(0, body.Size).ToArray());
      }
    }
    foreach (var handle in md.FieldDefinitions) {
      var field = md.GetFieldDefinition(handle);
      if (field.GetRelativeVirtualAddress() == 0) continue;
      var signature = md.GetBlobReader(field.Signature);
      if (signature.ReadByte() != 0x06 || signature.ReadByte() != 0x11) throw new Exception("Unsupported FieldRVA signature");
      var type = signature.ReadTypeHandle();
      if (type.Kind != HandleKind.TypeDefinition) throw new Exception("Unsupported FieldRVA type");
      var size = md.GetTypeDefinition((TypeDefinitionHandle)type).GetLayout().Size;
      if (size <= 0 || size > 20000000) throw new Exception("Invalid FieldRVA layout");
      output.Write(pe.GetSectionData(field.GetRelativeVirtualAddress()).GetContent(0, size).ToArray());
    }
    var resources = pe.PEHeaders.CorHeader.ResourcesDirectory;
    if (resources.Size != 0) output.Write(pe.GetSectionData(resources.RelativeVirtualAddress).GetContent(0, resources.Size).ToArray());
    return SHA256.HashData(output.ToArray());
  }
  static int Main(string[] args) => Identity(args[0]).SequenceEqual(Identity(args[1])) ? 0 : 1;
}`);
    await writeFile(join(dir, 'original.dll'), original); await writeFile(join(dir, 'rebuilt.dll'), rebuilt);
    execFileSync('dotnet', ['run', '--project', join(dir, 'verify.csproj'), '--configuration', 'Release', '--', join(dir, 'original.dll'), join(dir, 'rebuilt.dll')], { stdio: ['ignore', 'pipe', 'pipe'], timeout: 120000 });
  } catch (error) { throw new Error(`Conflicting DLL version, source or managed payload: ${String(error.stdout || '')}${String(error.stderr || '')}`); }
  finally { await rm(dir, { recursive: true, force: true }); }
}

export async function reconcileBundle({ github, plan, built, release }) {
  const assets = new Map(built), original = new Map();
  if (release) {
    for (const asset of await github.list(`${repoPath}/releases/${release.id}/assets`)) {
      requireState(assets.has(asset.name) && !original.has(asset.name) && Number.isSafeInteger(asset.id) && asset.state === 'uploaded' && asset.size > 0, `Conflicting or incomplete asset ${asset.name}`);
      const bytes = (await github.request(`${repoPath}/releases/assets/${asset.id}`, { binary: true })).data;
      requireState(bytes.length === asset.size && (!asset.digest || asset.digest === digest(bytes)), `Conflicting asset digest: ${asset.name}`);
      original.set(asset.name, bytes);
    }
    if (release?.draft === false) requireState(original.size === 4, 'Published release is incomplete; refusing mutation');
  }
  const dllName = 'Emby.M3uEditor.Plugin.dll', zipName = `m3u-editor-for-emby-${plan.version}.zip`;
  const dir = await mkdtemp(join(tmpdir(), 'release-bundle-'));
  try {
    for (const [name, bytes] of assets) { requireState(!name.includes('/'), 'Invalid asset path'); await writeFile(join(dir, name), bytes); }
    if (original.has(zipName)) {
      await writeFile(join(dir, zipName), original.get(zipName));
      const dll = execFileSync('python3', ['-c', 'import sys,zipfile; z=zipfile.ZipFile(sys.argv[1]); assert z.namelist()==[sys.argv[2]]; assert z.getinfo(sys.argv[2]).file_size < 20000000; sys.stdout.buffer.write(z.read(sys.argv[2]))', join(dir, zipName), dllName], { maxBuffer: 20000000 });
      requireState(!original.has(dllName) || original.get(dllName).equals(dll), 'Conflicting ZIP/DLL payload');
      original.set(dllName, dll);
    }
    if (original.has(dllName)) await verifyOriginalDll(original.get(dllName), built.get(dllName));
    for (const [name, bytes] of original) assets.set(name, bytes);
    if (!original.has(zipName) && original.has(dllName)) {
      await writeFile(join(dir, dllName), assets.get(dllName));
      execFileSync('python3', ['-c', 'import sys,pathlib,zipfile; e=zipfile.ZipInfo(sys.argv[2],(1980,1,1,0,0,0)); e.compress_type=zipfile.ZIP_DEFLATED; e.create_system=3; e.external_attr=0o100644<<16; z=zipfile.ZipFile(sys.argv[1],"w"); z.writestr(e,pathlib.Path(sys.argv[3]).read_bytes(),compresslevel=9); z.close()', join(dir, zipName), dllName, join(dir, dllName)]);
      assets.set(zipName, await readFile(join(dir, zipName)));
    }
    for (const [suffix, target, algorithm] of [['sha256', zipName, 'sha256'], ['md5', dllName, 'md5']]) {
      const name = `m3u-editor-for-emby-${plan.version}.${suffix}`;
      const hash = createHash(algorithm).update(assets.get(target)).digest('hex');
      if (original.has(name)) requireState(original.get(name).toString() === `${hash}  ${target}\n`, `Conflicting ${suffix} sidecar`);
      else assets.set(name, Buffer.from(`${hash}  ${target}\n`));
    }
    if (release?.draft === false) requireState(original.size === 4, 'Published release is incomplete; refusing mutation');
    return assets;
  } finally { await rm(dir, { recursive: true, force: true }); }
}

export async function runRelease({ cwd = process.cwd(), env = process.env, github = new GitHub(env.GITHUB_TOKEN) } = {}) {
  const branch = env.GITHUB_REF_NAME, controller = env.GITHUB_SHA;
  const authority = () => {
    execFileSync('bash', [join(root, 'scripts/release/verify-release-context.sh')], { cwd, env: gitEnvironment(env), stdio: ['ignore', 'pipe', 'pipe'] });
    requireState(git(cwd, 'rev-parse', 'HEAD') === controller, 'Controller HEAD differs from CI source');
    const origin = git(cwd, 'config', '--get', 'remote.origin.url');
    requireState([repositoryUrl, `https://github.com/${repository}`, `git@github.com:${repository}.git`, `ssh://git@github.com/${repository}.git`].includes(origin), 'Untrusted origin repository');
  };
  authority();
  await github.authenticate({ cwd, env });
  git(cwd, 'fetch', 'origin', 'refs/heads/main:refs/remotes/origin/main', 'refs/heads/develop:refs/remotes/origin/develop', 'refs/tags/*:refs/tags/*', 'refs/notes/*:refs/notes/*');
  const releases = await github.list(`${repoPath}/releases`);
  const tags = git(cwd, 'tag', '--merged', controller).split('\n').filter(tag => {
    const version = semver.valid(tag);
    return version && (branch === 'develop' ? semver.prerelease(version)?.[0] === 'beta' : !semver.prerelease(version));
  }).sort((a, b) => semver.compare(a, b));
  const channel = branch === 'develop' ? 'develop' : null;
  let frontier, frontierAssets;
  for (const tag of [...tags].reverse()) {
    const matches = releases.filter(release => release.tag_name === tag);
    requireState(matches.length <= 1, `Duplicate releases for ${tag}`);
    const release = matches[0];
    if (!release || release.draft !== false) continue;
    const source = git(cwd, 'rev-parse', `${tag}^{commit}`);
    requireState(release.name === tag && release.prerelease === (channel !== null) &&
      [source, branch].includes(release.target_commitish) && typeof release.body === 'string', `Conflicting published identity for ${tag}`);
    requireState(await remoteNotes(cwd, { gitTag: tag, gitHead: source, channel }), `Missing published channel notes for ${tag}`);
    // A published identity is a frontier only after its immutable bundle is verified, even before a newer plan.
    const publishedPlan = { version: semver.valid(tag), gitTag: tag, gitHead: source, channel };
    frontierAssets = await reconcileBundle({ github, plan: publishedPlan, built: await buildBundle({ cwd, plan: publishedPlan }), release });
    frontier = tag;
    break;
  }
  const incomplete = tags.filter(tag => (!frontier || semver.gt(tag, frontier)) &&
    !releases.some(release => release.tag_name === tag && release.draft === false));
  // Older gaps are superseded only by an authenticated, source-reachable channel frontier.
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
  const existing = releases.find(release => release.tag_name === plan.gitTag);
  if (existing && existing.draft !== false) requireState(existing.name === plan.gitTag && existing.target_commitish === plan.gitHead && existing.prerelease === (plan.channel !== null) && existing.body === plan.notes, 'Conflicting draft release identity or notes');
  const assets = plan.gitTag === frontier ? frontierAssets :
    await reconcileBundle({ github, plan, built: await buildBundle({ cwd, plan }), release: existing });
  authority();
  let remoteTag = git(cwd, 'ls-remote', 'origin', `refs/tags/${plan.gitTag}`, `refs/tags/${plan.gitTag}^{}`);
  if (!remoteTag) {
    if (!git(cwd, 'tag', '--list', plan.gitTag)) git(cwd, 'tag', plan.gitTag, plan.gitHead);
    requireState(git(cwd, 'rev-parse', `${plan.gitTag}^{commit}`) === plan.gitHead, 'Conflicting local tag source');
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
  const publishedIdentity = plan.gitTag === frontier ? releases.find(release => release.tag_name === frontier) : undefined;
  const beforeWrite = async () => {
    authority();
    const tag = git(cwd, 'ls-remote', 'origin', `refs/tags/${plan.gitTag}`, `refs/tags/${plan.gitTag}^{}`);
    requireState(tag.split('\n').at(-1)?.split(/\s+/)[0] === plan.gitHead, 'Remote tag source changed');
    requireState(await remoteNotes(cwd, plan), 'Remote channel notes changed');
  };
  const release = await publishRelease({ github, plan, assets, publishedIdentity, beforeWrite, cwd, env });
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
      let main = git(cwd, 'rev-parse', 'refs/remotes/origin/main');
      if (excludeTag && !ancestor(cwd, main, source)) {
        const bases = git(cwd, 'merge-base', '--all', main, source).split('\n');
        requireState(bases.length === 1 && /^[0-9a-f]{40}$/.test(bases[0]), 'Historical main ancestry is ambiguous');
        main = bases[0];
      }
      git(remote, 'fetch', '--no-tags', cwd, main);
      git(remote, 'update-ref', 'refs/heads/main', main);
    }
    const planningTags = new Set(git(cwd, 'tag', '--merged', source).split('\n').filter(Boolean));
    if (branch === 'develop' && !excludeTag) for (const tag of git(cwd, 'tag', '--merged', 'refs/remotes/origin/main').split('\n').filter(Boolean)) planningTags.add(tag);
    for (const tag of planningTags) {
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

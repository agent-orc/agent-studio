import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { inventory, relocate } from './relocate-workspace-paths.mjs';

test('mapping uses a whole path prefix and preserves a root target', () => {
  const mapping = [['C:\\work', '/data/workspace'], ['D:\\', '/']];
  assert.equal(relocate('C:\\work\\task', mapping).target, '/data/workspace/task');
  assert.equal(relocate('C:\\worker\\task', mapping).unresolved, true);
  assert.equal(relocate('D:\\', mapping).target, '/');
});

test('relocates registry backup when the primary registry is absent', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'relocate-registry-backup-'));
  try {
    const workspace = path.join(root, 'workspace');
    await mkdir(path.join(workspace, '.metadata'), { recursive: true });
    const backup = path.join(workspace, '.metadata', 'projects.json.bak');
    const original = JSON.stringify({ Projects: [{ RepositoryPath: 'C:\\code\\demo' }] }) + '\n';
    await writeFile(backup, original);
    const mapping = path.join(root, 'map.json');
    await writeFile(mapping, JSON.stringify({ 'D:\\code': '/data/projects' }));
    let report = await inventory({ workspace, mapping });
    assert.equal(report.entries.length, 1);
    assert.equal(report.entries[0].source, 'C:\\code\\demo');
    assert.equal(report.unresolved, 1);
    await assert.rejects(inventory({ workspace, mapping, apply: true }), /unresolved/);
    assert.equal(await readFile(backup, 'utf8'), original);
    await writeFile(mapping, JSON.stringify({ 'C:\\code': '/data/projects' }));
    report = await inventory({ workspace, mapping, apply: true });
    assert.equal(report.unresolved, 0);
    assert.deepEqual(report.changedFiles, [backup]);
    assert.equal(JSON.parse(await readFile(backup, 'utf8')).Projects[0].RepositoryPath, '/data/projects/demo');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('inventories paths, refuses unresolved paths, backs up and relocates without formatting drift', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'relocate-workspace-'));
  try {
    const workspace = path.join(root, 'workspace');
    const checkout = path.join(root, 'checkout');
    await mkdir(path.join(workspace, '.metadata'), { recursive: true });
    await mkdir(checkout);
    const registry = path.join(workspace, '.metadata', 'projects.json');
    const original = '{\r\n  "projects": [{"StorageLocation":"C:\\\\old\\\\workspace\\\\projects\\\\demo", "RepositoryPath":"D:\\\\code\\\\demo", "RootPath":"D:\\\\code\\\\demo"}]\r\n}\r\n';
    await writeFile(registry, original);
    const link = path.join(checkout, '.git');
    await writeFile(link, 'gitdir: D:\\code\\demo\\.git\\worktrees\\slot\n');
    const worktreeConfig = path.join(root, 'config.worktree');
    await writeFile(worktreeConfig, '[core]\n\tworktree = D:\\code\\demo\n');
    const config = path.join(root, 'appsettings.Production.json');
    await writeFile(config, '{"TaskRepository":"C:\\\\old\\\\workspace","WatchPaths":[{"Path":"C:\\\\old\\\\workspace\\\\projects\\\\demo"}]}\n');
    const mapping = path.join(root, 'map.json');
    await writeFile(mapping, JSON.stringify({ 'C:\\old\\workspace': '/data/workspace' }));
    let report = await inventory({ workspace, mapping, config: [config], checkout: [checkout], link: [worktreeConfig] });
    assert.equal(report.unresolved, 4);
    assert.equal(report.entries.length, 7);
    await assert.rejects(inventory({ workspace, mapping, config: [config], checkout: [checkout], link: [worktreeConfig], apply: true }), /unresolved/);
    assert.equal(await readFile(registry, 'utf8'), original);
    await writeFile(mapping, JSON.stringify({ 'C:\\old\\workspace': '/data/workspace', 'D:\\code': '/data/projects' }));
    report = await inventory({ workspace, mapping, config: [config], checkout: [checkout], link: [worktreeConfig], apply: true });
    assert.equal(report.unresolved, 0);
    assert.equal(report.changedFiles.length, 4);
    assert.ok(report.backup);
    assert.equal((await readFile(registry, 'utf8')).includes('\r\n'), true);
    assert.equal((await readFile(registry, 'utf8')).includes('/data/projects/demo'), true);
    assert.equal(await readFile(link, 'utf8'), 'gitdir: /data/projects/demo/.git/worktrees/slot\n');
    assert.equal(await readFile(worktreeConfig, 'utf8'), '[core]\n\tworktree = /data/projects/demo\n');
    const manifest = JSON.parse(await readFile(path.join(report.backup, 'manifest.json'), 'utf8'));
    assert.equal(manifest.length, 4);
    assert.equal(await readFile(manifest.find(item => item.source === registry).backup, 'utf8'), original);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

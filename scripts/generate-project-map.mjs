#!/usr/bin/env node

import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';

const DOCUMENTATION_GENERATOR_VERSION = 'project-map-doc-v1';
const DEFAULT_OUTPUT = 'docs/system/architecture/project-map.md';
const DEFAULT_HISTORY = 'docs/system/architecture/project-map-history';

function parseArguments(argv) {
  const options = {
    api: 'http://127.0.0.1:5030',
    project: 'AGT',
    output: DEFAULT_OUTPUT,
    historyDir: DEFAULT_HISTORY,
    snapshot: null,
    capture: false,
  };
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    const value = argv[index + 1];
    if (argument === '--api' && value) options.api = value, index += 1;
    else if (argument === '--project' && value) options.project = value, index += 1;
    else if (argument === '--output' && value) options.output = value, index += 1;
    else if (argument === '--history-dir' && value) options.historyDir = value, index += 1;
    else if (argument === '--snapshot' && value) options.snapshot = value, index += 1;
    else if (argument === '--capture') options.capture = true;
    else if (argument === '--help') {
      console.log('Usage: node scripts/generate-project-map.mjs [--api URL --project ID_OR_ALIAS] [--capture] [--snapshot FILE] [--output FILE] [--history-dir DIR]');
      process.exit(0);
    } else {
      throw new Error(`Unknown or incomplete argument: ${argument}`);
    }
  }
  return options;
}

async function loadSnapshot(options) {
  if (options.snapshot) {
    const payload = JSON.parse(await readFile(path.resolve(options.snapshot), 'utf8'));
    const snapshot = payload.snapshot ?? payload;
    return {
      snapshot,
      source: { kind: 'snapshot-import', snapshotId: snapshot.snapshotId ?? payload.snapshotId ?? null },
    };
  }
  const base = `${options.api.replace(/\/$/, '')}/api/projects/${encodeURIComponent(options.project)}/graph`;
  const endpoint = options.capture ? `${base}/captures` : base;
  const response = await fetch(endpoint, {
    method: options.capture ? 'POST' : 'GET',
    headers: { accept: 'application/json' },
  });
  if (!response.ok) throw new Error(`Project Graph request failed: ${response.status} ${response.statusText}`);
  return {
    snapshot: await response.json(),
    source: { kind: options.capture ? 'explicit-api-capture' : 'persisted-api-current', project: options.project },
  };
}

function technologySlug(label) {
  const normalized = String(label).toLowerCase();
  if (normalized.startsWith('.net')) return 'dotnet';
  if (normalized === 'c#') return 'csharp';
  if (normalized.startsWith('asp.net')) return 'aspnet-core';
  if (normalized.startsWith('angular')) return 'angular';
  if (normalized === 'github actions') return 'github-actions';
  if (normalized === 'next.js') return 'nextjs';
  return normalized.replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
}

function normalizedTechnologies(values, kind = '') {
  const bySlug = new Map();
  for (const value of values ?? []) {
    const technology = typeof value === 'string'
      ? { slug: technologySlug(value), label: value }
      : value;
    const current = bySlug.get(technology.slug);
    if (!current || technology.label.length > current.label.length) bySlug.set(technology.slug, technology);
  }
  if (kind === 'dotnet') bySlug.set('csharp', { slug: 'csharp', label: 'C#' });
  return [...bySlug.values()].sort((left, right) => left.slug.localeCompare(right.slug));
}

function stableComponentId(projectId, kind, source) {
  const slug = source.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '');
  return `${projectId.toLowerCase()}:${kind}:${slug}`;
}

function normalizeSnapshot(snapshot) {
  const projects = (snapshot.projects ?? []).map(project => {
    const normalized = { ...project };
    delete normalized.repositoryName;
    return {
      ...normalized,
      shortCode: project.shortCode ?? project.key ?? '',
      repositoryLabel: project.repositoryLabel ?? `${project.id} · ${project.displayName}`,
      technologies: normalizedTechnologies(project.technologies),
    };
  });
  const projectByAlias = new Map(projects.flatMap(project => [[project.key, project], [project.shortCode, project]]));
  const idMap = new Map();
  const components = (snapshot.components ?? []).map(component => {
    const project = projectByAlias.get(component.projectKey);
    const projectId = component.projectId ?? project?.id ?? component.projectKey;
    const id = stableComponentId(projectId, component.kind, component.relativePath);
    idMap.set(component.id, id);
    return {
      ...component,
      id,
      projectId,
      technologies: normalizedTechnologies(component.technologies, component.kind),
    };
  });
  const componentByProject = new Map();
  for (const component of components) {
    const values = componentByProject.get(component.projectId) ?? [];
    values.push(component.id);
    componentByProject.set(component.projectId, values);
  }
  const normalizedProjects = projects.map(project => {
    const componentTechnologies = components
      .filter(component => component.projectId === project.id)
      .flatMap(component => component.technologies);
    return {
      ...project,
      technologies: normalizedTechnologies([...project.technologies, ...componentTechnologies]),
      componentIds: (componentByProject.get(project.id) ?? []).sort(),
    };
  });
  const focus = normalizedProjects.find(project => project.id === snapshot.focusProjectId)
    ?? normalizedProjects.find(project => project.key === snapshot.focusProjectKey);
  return {
    ...snapshot,
    snapshotId: snapshot.snapshotId ?? `pg-import-${String(snapshot.capturedAtUtc).replace(/[^0-9]/g, '')}`,
    previousSnapshotId: snapshot.previousSnapshotId ?? null,
    captureMode: snapshot.captureMode ?? 'imported',
    focusProjectId: snapshot.focusProjectId ?? focus?.id ?? '',
    projects: normalizedProjects,
    components,
    dependencies: (snapshot.dependencies ?? []).map(dependency => ({
      ...dependency,
      fromComponentId: idMap.get(dependency.fromComponentId) ?? dependency.fromComponentId,
      toComponentId: dependency.toComponentId ? idMap.get(dependency.toComponentId) ?? dependency.toComponentId : null,
      resolution: dependency.resolution ?? (dependency.toComponentId ? 'resolved' : 'unresolved'),
      targetHint: dependency.targetHint ?? null,
    })),
  };
}

function validate(snapshot) {
  if (!snapshot || typeof snapshot !== 'object') throw new Error('Snapshot must be a JSON object.');
  if (!Number.isInteger(snapshot.schemaVersion)) throw new Error('Snapshot schemaVersion is missing.');
  if (typeof snapshot.generatorVersion !== 'string') throw new Error('Snapshot generatorVersion is missing.');
  if (typeof snapshot.snapshotId !== 'string' || snapshot.snapshotId.length === 0) throw new Error('Snapshot snapshotId is missing.');
  if (!Array.isArray(snapshot.projects) || !Array.isArray(snapshot.components) || !Array.isArray(snapshot.dependencies)) {
    throw new Error('Snapshot projects, components, and dependencies must be arrays.');
  }
  if (Number.isNaN(Date.parse(snapshot.capturedAtUtc))) throw new Error('Snapshot capturedAtUtc is invalid.');
}

function redactLocalText(value) {
  return String(value ?? '')
    .replace(/file:(?!<local-path>).*$/gi, 'file:<local-path>')
    .replace(/[a-z]:[\\/].*$/gi, '<local-path>')
    .replace(/(?:\\\\|\/\/)[^\r\n]*$/g, '<local-path>')
    .replace(/(^|[\s:(])\/(?!\/)[^\r\n]*$/g, '$1<local-path>');
}

function redactLocalEvidence(snapshot) {
  return {
    ...snapshot,
    dependencies: snapshot.dependencies.map(dependency => ({
      ...dependency,
      evidence: redactLocalText(dependency.evidence),
      targetHint: dependency.targetHint ? redactLocalText(dependency.targetHint) : null,
    })),
  };
}

function cell(value) {
  if (value === null || value === undefined || value === '') return 'Unavailable';
  return String(value).replaceAll('|', '\\|').replaceAll('\r', ' ').replaceAll('\n', ' ');
}

function code(value) {
  return `\`${String(value).replaceAll('`', '\\`')}\``;
}

function count(value) {
  return new Intl.NumberFormat('en-US').format(value ?? 0);
}

function revision(project, full = false) {
  if (!project.sourceRevision) return 'Unavailable';
  return full ? code(project.sourceRevision) : code(project.sourceRevision.slice(0, 12));
}

function componentLabel(component) {
  return component ? `${component.projectKey} / ${component.name}` : 'Unavailable component';
}

function technologyLabels(technologies) {
  return technologies?.map(technology => `${technology.label} (${technology.slug})`).join(', ') || 'None detected';
}

function renderMarkdown(snapshot, generatedAtUtc, historyReference, source) {
  const byComponent = new Map(snapshot.components.map(component => [component.id, component]));
  const lines = [
    '# Project map',
    '',
    `> Read-only repository inventory captured ${code(snapshot.capturedAtUtc)}. Documentation generated ${code(generatedAtUtc)}.`,
    `> Snapshot schema ${code(snapshot.schemaVersion)}, discovery ${code(snapshot.generatorVersion)}, documentation ${code(DOCUMENTATION_GENERATOR_VERSION)}.`,
    `> Snapshot ${code(snapshot.snapshotId)} (previous: ${code(snapshot.previousSnapshotId ?? 'none')}); source ${code(source.kind)}; history ${code(historyReference)}.`,
    '',
    '## Scope and interpretation',
    '',
    'This map inventories supported solution, project, package, Angular workspace, and GitHub Actions manifest files across every managed project row returned by the registry. Workflow paths prove manifest presence only; they do not assert that a workflow is valid, enabled, or operational.',
    '',
    'File and line counts are rough, overlapping component estimates. Generated output, dependency directories, build output, nested repositories, and directory links are excluded. Resolved internal dependencies and unresolved local manifest references are shown separately. This is not a code-call graph, runtime trace, architecture grade, or claim that all projects share one revision.',
    '',
    `Canonical project identity is the registry ID (${code('PROJ-NNN')}); short codes and display names are mutable aliases. Technology identity uses the parenthesized canonical slug, such as ${code('dotnet')}, ${code('csharp')}, and ${code('angular')}.`,
    '',
    '## Portfolio summary',
    '',
    '| Project ID | Short code | Project | Discovery | Components | Solutions | Workflow manifests | Technologies | Rough size |',
    '| --- | --- | --- | --- | ---: | ---: | ---: | --- | ---: |',
  ];

  for (const project of snapshot.projects) {
    lines.push(`| ${cell(project.id)} | ${cell(project.shortCode || project.key)} | ${cell(project.displayName)} | ${cell(project.status)} | ${count(project.componentIds?.length)} | ${count(project.solutions?.length)} | ${count(project.workflows?.length)} | ${cell(technologyLabels(project.technologies))} | ${count(project.size?.files)} files / ${count(project.size?.lines)} LoC |`);
  }

  lines.push('', '## Source provenance', '',
    'Each managed repository has independent provenance. A dirty state means the manifest inventory may include tracked or untracked working-tree content beyond the recorded revision.',
    '',
    '| Project ID | Short code | Repository | Revision | Working tree | Captured |',
    '| --- | --- | --- | --- | --- | --- |');
  for (const project of snapshot.projects) {
    lines.push(`| ${cell(project.id)} | ${cell(project.shortCode || project.key)} | ${cell(project.repositoryLabel)} | ${revision(project, true)} | ${cell(project.sourceState)} | ${code(snapshot.capturedAtUtc)} |`);
  }

  lines.push('', '## Components', '');
  for (const project of snapshot.projects) {
    const components = snapshot.components.filter(component => component.projectKey === project.key);
    lines.push(`### ${project.key}: ${project.displayName}`, '');
    if (project.warnings?.length) lines.push(`Discovery notes: ${project.warnings.map(cell).join('; ')}`, '');
    lines.push('| Component | Kind | Manifest | Technologies | Rough size |', '| --- | --- | --- | --- | ---: |');
    if (components.length === 0) {
      lines.push('| No supported component manifest discovered | - | - | - | 0 files / 0 LoC |');
    } else {
      for (const component of components) {
        lines.push(`| ${cell(component.name)} | ${cell(component.kind)} | ${code(component.relativePath)} | ${cell(technologyLabels(component.technologies))} | ${count(component.size?.files)} files / ${count(component.size?.lines)} LoC |`);
      }
    }
    if (project.solutions?.length) lines.push('', `Solutions: ${project.solutions.map(code).join(', ')}`);
    if (project.workflows?.length) lines.push('', `Workflows: ${project.workflows.map(code).join(', ')}`);
    lines.push('');
  }

  lines.push('## Manifest relations', '', '| Source | Target | Resolution | Kind | Evidence |', '| --- | --- | --- | --- | --- |');
  if (snapshot.dependencies.length === 0) {
    lines.push('| None discovered | None discovered | - | - | - |');
  } else {
    for (const dependency of snapshot.dependencies) {
      const target = dependency.toComponentId
        ? componentLabel(byComponent.get(dependency.toComponentId))
        : dependency.targetHint ?? 'Unresolved local target';
      lines.push(`| ${cell(componentLabel(byComponent.get(dependency.fromComponentId)))} | ${cell(target)} | ${cell(dependency.resolution)} | ${cell(dependency.kind)} | ${code(dependency.evidence)} |`);
    }
  }

  lines.push('', '## Regeneration', '', 'Render the persisted current capture without walking repositories:', '',
    '```sh', 'node scripts/generate-project-map.mjs --api http://127.0.0.1:5030 --project PROJ-002', '```', '',
    'Create a fresh explicit portfolio capture, persist it as current plus API history, and render that exact snapshot:', '',
    '```sh', 'node scripts/generate-project-map.mjs --api http://127.0.0.1:5030 --project PROJ-002 --capture', '```', '',
    'For a reviewed or archived API response:', '', '```sh',
    'node scripts/generate-project-map.mjs --snapshot path/to/project-graph.json', '```', '',
    `Each documentation command atomically replaces ${code(DEFAULT_OUTPUT)} and appends a dated JSON provenance envelope under ${code(DEFAULT_HISTORY)}.`, '');
  return lines.join('\n');
}

function historyFileName(generatedAtUtc) {
  return `${generatedAtUtc.replaceAll(':', '-').replaceAll('.', '-')}.json`;
}

async function atomicWrite(filePath, content) {
  const temporaryPath = `${filePath}.${process.pid}.${Date.now()}.tmp`;
  try {
    await writeFile(temporaryPath, content, 'utf8');
    await rename(temporaryPath, filePath);
  } finally {
    await rm(temporaryPath, { force: true });
  }
}

const options = parseArguments(process.argv.slice(2));
const loaded = await loadSnapshot(options);
const snapshot = redactLocalEvidence(normalizeSnapshot(loaded.snapshot));
validate(snapshot);
const generatedAtUtc = new Date().toISOString();
const outputPath = path.resolve(options.output);
const historyDirectory = path.resolve(options.historyDir);
const historyPath = path.join(historyDirectory, historyFileName(generatedAtUtc));
const historyReference = path.relative(process.cwd(), historyPath).replaceAll('\\', '/');
const outputReference = path.relative(process.cwd(), outputPath).replaceAll('\\', '/');
const markdown = renderMarkdown(snapshot, generatedAtUtc, historyReference, loaded.source);
await mkdir(path.dirname(outputPath), { recursive: true });
await mkdir(historyDirectory, { recursive: true });
await atomicWrite(historyPath, `${JSON.stringify({
  documentationGeneratorVersion: DOCUMENTATION_GENERATOR_VERSION,
  generatedAtUtc,
  snapshotId: snapshot.snapshotId,
  previousSnapshotId: snapshot.previousSnapshotId ?? null,
  source: loaded.source,
  currentDocument: outputReference,
  historyRecord: historyReference,
  snapshot,
}, null, 2)}\n`);
await atomicWrite(outputPath, `${markdown.trimEnd()}\n`, 'utf8');
console.log(`Wrote ${path.relative(process.cwd(), outputPath)}`);
console.log(`Wrote ${path.relative(process.cwd(), historyPath)}`);

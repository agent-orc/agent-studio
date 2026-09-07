#!/usr/bin/env node

import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '../..');
const sourceRoot = path.join(root, 'frontend/src/app');

function walk(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) return walk(target);
    return entry.isFile() && entry.name.endsWith('.ts') && !entry.name.endsWith('.spec.ts')
      ? [target]
      : [];
  });
}

function skipSpace(source, offset) {
  while (/\s/.test(source[offset] ?? '')) offset += 1;
  return offset;
}

function skipBalanced(source, offset, open, close) {
  let depth = 0;
  let quote = null;
  let escaped = false;
  for (let i = offset; i < source.length; i += 1) {
    const char = source[i];
    if (quote) {
      if (escaped) escaped = false;
      else if (char === '\\') escaped = true;
      else if (char === quote) quote = null;
      continue;
    }
    if (char === '"' || char === "'" || char === '`') {
      quote = char;
      continue;
    }
    if (char === open) depth += 1;
    if (char === close && --depth === 0) return i + 1;
  }
  return source.length;
}

function firstArgument(source, openParen) {
  let round = 0;
  let square = 0;
  let curly = 0;
  let quote = null;
  let escaped = false;
  for (let i = openParen + 1; i < source.length; i += 1) {
    const char = source[i];
    if (quote) {
      if (escaped) escaped = false;
      else if (char === '\\') escaped = true;
      else if (char === quote) quote = null;
      continue;
    }
    if (char === '"' || char === "'" || char === '`') quote = char;
    else if (char === '(') round += 1;
    else if (char === ')') {
      if (round === 0 && square === 0 && curly === 0) return source.slice(openParen + 1, i).trim();
      round -= 1;
    } else if (char === '[') square += 1;
    else if (char === ']') square -= 1;
    else if (char === '{') curly += 1;
    else if (char === '}') curly -= 1;
    else if (char === ',' && round === 0 && square === 0 && curly === 0) {
      return source.slice(openParen + 1, i).trim();
    }
  }
  return '';
}

function findAssignment(source, identifier, before) {
  const prefix = source.slice(0, before);
  const pattern = new RegExp(`(?:const|let)\\s+${identifier}\\s*=`, 'g');
  let match;
  let last = null;
  while ((match = pattern.exec(prefix))) last = match;
  if (!last) return null;
  const start = skipSpace(source, last.index + last[0].length);
  let round = 0;
  let square = 0;
  let curly = 0;
  let quote = null;
  let escaped = false;
  for (let i = start; i < before; i += 1) {
    const char = source[i];
    if (quote) {
      if (escaped) escaped = false;
      else if (char === '\\') escaped = true;
      else if (char === quote) quote = null;
      continue;
    }
    if (char === '"' || char === "'" || char === '`') quote = char;
    else if (char === '(') round += 1;
    else if (char === ')') round -= 1;
    else if (char === '[') square += 1;
    else if (char === ']') square -= 1;
    else if (char === '{') curly += 1;
    else if (char === '}') curly -= 1;
    else if (char === ';' && round === 0 && square === 0 && curly === 0) return source.slice(start, i).trim();
  }
  return null;
}

function placeholder(expression) {
  const argumentOf = (name) => {
    const marker = `${name}(`;
    const start = expression.indexOf(marker);
    if (start < 0) return null;
    let value = expression.slice(start + marker.length).trim();
    if (value.endsWith(')')) value = value.slice(0, -1).trim();
    return value;
  };
  const decoded = argumentOf('encodeURIComponent');
  if (decoded) {
    const name = decoded.replace(/^this\./, '').replace(/\(\)$/, '').replace(/\.id$/, 'Id');
    return `{${name}}`;
  }
  const relPath = argumentOf('encodeRelPath');
  if (relPath) return `{${relPath}*}`;
  if (/orchestratorContextChatSegment/.test(expression)) return '{contextKey}';
  if (/\bqs\b/.test(expression)) return '{?trigger,severity,topic,limit}';
  if (/\bphase\b/.test(expression)) return '{phase}';
  if (/\baction\b/.test(expression)) return '{action}';
  const simple = expression.replace(/\(\)$/g, '').match(/([A-Za-z_$][\w$]*)\s*$/)?.[1];
  return `{${simple ?? 'value'}}`;
}

function normalise(expression, source, before, baseUrl, depth = 0) {
  if (!expression || depth > 4) return null;
  let value = expression.trim().replace(/[\r\n]+/g, ' ').replace(/\s+/g, ' ');
  if (/^[A-Za-z_$][\w$]*$/.test(value)) {
    const assignment = findAssignment(source, value, before);
    if (assignment) return normalise(assignment, source, before, baseUrl, depth + 1);
    return null;
  }
  value = value.replace(/\$\{this\.baseUrl\}/g, baseUrl);
  value = value.replace(/\$\{([^}]+)\}/g, (_, part) => placeholder(part));
  value = value.replace(/^(["'`])|(["'`])$/g, '');
  value = value.replace(/\s*\+\s*/g, '');
  value = value.replace(/["'`]/g, '');
  if (value.startsWith('/api') || value.startsWith('/hubs')) return value;
  return null;
}

function lineAt(source, offset) {
  return source.slice(0, offset).split('\n').length;
}

const candidates = [];
const unresolved = [];
for (const file of walk(sourceRoot).sort()) {
  const source = fs.readFileSync(file, 'utf8');
  const relative = path.relative(root, file).replaceAll(path.sep, '/');
  const baseMatch = source.match(/private readonly baseUrl\s*=\s*['"]([^'"]+)['"]/);
  const baseUrl = baseMatch?.[1] ?? (source.includes('private readonly baseUrl = (() =>') ? '[external]' : '/api');
  const matcher = /(?:this\.)?http!?\s*\.(get|post|put|patch|delete)|\.withUrl|sessionFetch|this\.(getUtf8Text|conditionalGet)/g;
  let match;
  while ((match = matcher.exec(source))) {
    let offset = skipSpace(source, matcher.lastIndex);
    if (source[offset] === '<') offset = skipSpace(source, skipBalanced(source, offset, '<', '>'));
    if (source[offset] !== '(') continue;
    const expression = firstArgument(source, offset);
    const route = normalise(expression, source, match.index, baseUrl);
    let method = match[1]?.toUpperCase() ?? 'GET';
    if (match[0] === 'sessionFetch') {
      const callTail = source.slice(offset, Math.min(source.length, offset + 600));
      method = callTail.match(/method:\s*['"]([A-Z]+)['"]/)?.[1] ?? 'GET';
    }
    if (match[2] === 'getUtf8Text' || match[2] === 'conditionalGet') method = 'GET';
    if (match[0] === '.withUrl') method = 'WS';
    if (!route) {
      unresolved.push({ method, expression, evidence: `${relative}:${lineAt(source, match.index)}` });
      continue;
    }
    candidates.push({ method, path: route, evidence: `${relative}:${lineAt(source, match.index)}` });
  }
}

const grouped = new Map();
for (const item of candidates) {
  const canonicalPath = item.path === '/api/clients/' ? '/api/clients' : item.path;
  const key = `${item.method} ${canonicalPath}`;
  const current = grouped.get(key) ?? { method: item.method, path: canonicalPath, evidence: [] };
  if (!current.evidence.includes(item.evidence)) current.evidence.push(item.evidence);
  grouped.set(key, current);
}
const unique = [...grouped.values()]
  .sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method));
process.stdout.write(`${JSON.stringify({ generatedAt: new Date().toISOString(), count: unique.length, routes: unique, unresolved }, null, 2)}\n`);

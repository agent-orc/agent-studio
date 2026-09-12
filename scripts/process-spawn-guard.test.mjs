import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const scannedRoots = ['scripts', 'frontend/tests', 'update-service'];
const guardedFunctions = new Set([
  'spawn', 'spawnSync', 'exec', 'execSync', 'execFile', 'execFileSync', 'fork',
]);

test('every Node child-process launch hides its Windows console', () => {
  const violations = [];
  for (const root of scannedRoots) {
    const absolute = join(repository, root);
    if (!existsSync(absolute)) continue;
    for (const file of walkMjs(absolute)) {
      const source = readFileSync(file, 'utf8');
      for (const call of scanChildProcessCalls(source)) {
        if (!/\bwindowsHide\s*:\s*true\b/u.test(call.arguments)) {
          violations.push(`${relative(repository, file).split('\\').join('/')}:${call.line} (${call.name})`);
        }
      }
    }
  }

  assert.deepEqual(violations, [],
    `child_process launches must pass windowsHide: true:\n  ${violations.join('\n  ')}`);
});

test('scanner rejects an unguarded spawn and accepts a guarded spawn', () => {
  const unguarded = "import { spawn } from 'node:child_process';\nspawn('git', ['status'], { stdio: 'pipe' });";
  const guarded = "import { spawn } from 'node:child_process';\nspawn('git', ['status'], { stdio: 'pipe', windowsHide: true });";

  assert.doesNotMatch(scanChildProcessCalls(unguarded)[0].arguments, /\bwindowsHide\s*:\s*true\b/u);
  assert.match(scanChildProcessCalls(guarded)[0].arguments, /\bwindowsHide\s*:\s*true\b/u);
});

function walkMjs(root) {
  const files = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    if (entry.name === 'node_modules') continue;
    const path = join(root, entry.name);
    if (entry.isDirectory()) files.push(...walkMjs(path));
    else if (extname(entry.name) === '.mjs') files.push(path);
  }
  return files.sort();
}

function scanChildProcessCalls(source) {
  const bindings = childProcessBindings(source);
  if (bindings.size === 0) return [];
  const code = maskCommentsAndStrings(source.replaceAll('\r\n', '\n'));
  const calls = [];
  for (const [localName, importedName] of bindings) {
    const marker = new RegExp(`\\b${escapeRegex(localName)}\\s*\\(`, 'gu');
    for (const match of code.matchAll(marker)) {
      const open = code.indexOf('(', match.index);
      const close = balancedClose(code, open);
      calls.push({
        name: importedName,
        line: code.slice(0, match.index).split('\n').length,
        arguments: close < 0 ? code.slice(open + 1) : code.slice(open + 1, close),
      });
    }
  }
  return calls;
}

function childProcessBindings(source) {
  const bindings = new Map();
  const imports = /import\s*\{([^}]+)\}\s*from\s*['"](?:node:)?child_process['"]/gu;
  for (const match of source.matchAll(imports)) {
    addNamedBindings(bindings, match[1], /\s+as\s+/u);
  }
  const destructuredRequires = /(?:const|let|var)\s*\{([^}]+)\}\s*=\s*require\(\s*['"](?:node:)?child_process['"]\s*\)/gu;
  for (const match of source.matchAll(destructuredRequires)) {
    addNamedBindings(bindings, match[1], /\s*:\s*/u);
  }
  const namespaces = [
    ...source.matchAll(/import\s*\*\s*as\s+(\w+)\s*from\s*['"](?:node:)?child_process['"]/gu),
    ...source.matchAll(/(?:const|let|var)\s+(\w+)\s*=\s*require\(\s*['"](?:node:)?child_process['"]\s*\)/gu),
  ];
  for (const match of namespaces) {
    for (const functionName of guardedFunctions) {
      bindings.set(`${match[1]}.${functionName}`, functionName);
    }
  }
  for (const [localName, importedName] of [...bindings]) {
    const promisified = new RegExp(`(?:const|let|var)\\s+(\\w+)\\s*=\\s*promisify\\(\\s*${escapeRegex(localName)}\\s*\\)`, 'gu');
    for (const match of source.matchAll(promisified)) bindings.set(match[1], importedName);
  }
  return bindings;
}

function addNamedBindings(bindings, list, separator) {
  for (const item of list.split(',')) {
    const [importedName, localName = importedName] = item.trim().split(separator);
    if (guardedFunctions.has(importedName)) bindings.set(localName, importedName);
  }
}

function balancedClose(code, open) {
  let depth = 0;
  for (let index = open; index < code.length; index++) {
    if (code[index] === '(') depth++;
    else if (code[index] === ')' && --depth === 0) return index;
  }
  return -1;
}

function maskCommentsAndStrings(source) {
  const chars = [...source];
  for (let index = 0; index < chars.length;) {
    if (chars[index] === '/' && chars[index + 1] === '/') {
      const end = source.indexOf('\n', index + 2);
      mask(chars, index, end < 0 ? chars.length : end);
      index = end < 0 ? chars.length : end;
    } else if (chars[index] === '/' && chars[index + 1] === '*') {
      const found = source.indexOf('*/', index + 2);
      const end = found < 0 ? chars.length : found + 2;
      mask(chars, index, end);
      index = end;
    } else if (chars[index] === "'" || chars[index] === '"' || chars[index] === '`') {
      const quote = chars[index];
      let end = index + 1;
      while (end < chars.length) {
        if (chars[end] === '\\') end += 2;
        else if (chars[end] === quote) { end++; break; }
        else end++;
      }
      mask(chars, index, Math.min(end, chars.length));
      index = end;
    } else {
      index++;
    }
  }
  return chars.join('');
}

function mask(chars, start, end) {
  for (let index = start; index < end; index++) {
    if (chars[index] !== '\n') chars[index] = ' ';
  }
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
}

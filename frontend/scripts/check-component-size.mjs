#!/usr/bin/env node
/**
 * Enforce Angular component size budgets across the component's controller,
 * template, and stylesheet files.
 *
 * ESLint can limit individual files, but it cannot see that
 * `foo.ts`, `foo.html`, and `foo.scss` form one component. This guard closes
 * that gap. Existing oversized components are listed in the baseline and may
 * not grow beyond it; new or already-small components must stay under the
 * global limits.
 */
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, normalize } from 'node:path';
import { execSync } from 'node:child_process';

const baseline = JSON.parse(
  readFileSync(new URL('./component-size-baseline.json', import.meta.url), 'utf8'),
);

const limits = baseline.limits;
const baselineComponents = baseline.components ?? {};
const roots = ['src/app', 'src/mockups', 'projects/studio-ui/src', 'projects/studio-ui-catalogue/src'];
const errors = [];
const scanned = new Map();

function lineCount(file) {
  return readFileSync(file, 'utf8').split(/\r?\n/).length;
}

function normalizePath(file) {
  return file.replaceAll('\\', '/');
}

function firstMetadataUrl(text, key) {
  const match = text.match(new RegExp(`${key}\\s*:\\s*[\`'"]([^\`'"]+)[\`'"]`));
  return match?.[1] ?? null;
}

function styleMetadataUrls(text) {
  const urls = [];
  const styleUrl = firstMetadataUrl(text, 'styleUrl');
  if (styleUrl) urls.push(styleUrl);

  const styleUrls = text.match(/styleUrls\s*:\s*\[([\s\S]*?)\]/);
  if (styleUrls) {
    for (const match of styleUrls[1].matchAll(/[\`'"]([^\`'"]+)[\`'"]/g)) {
      urls.push(match[1]);
    }
  }

  return urls;
}

function componentMetadataText(text) {
  const markerIndex = text.indexOf('@Component');
  if (markerIndex < 0) return '';

  const openParen = text.indexOf('(', markerIndex);
  const openBrace = text.indexOf('{', openParen);
  if (openParen < 0 || openBrace < 0) return '';

  let depth = 0;
  let quote = null;
  let escaped = false;

  for (let index = openBrace; index < text.length; index += 1) {
    const char = text[index];

    if (quote) {
      if (escaped) {
        escaped = false;
      } else if (char === '\\') {
        escaped = true;
      } else if (char === quote) {
        quote = null;
      }
      continue;
    }

    if (char === '\'' || char === '"' || char === '`') {
      quote = char;
      continue;
    }

    if (char === '{') {
      depth += 1;
    } else if (char === '}') {
      depth -= 1;
      if (depth === 0) {
        return text.slice(openBrace, index + 1);
      }
    }
  }

  return '';
}

function resolveSidecar(componentFile, relativeUrl, kind) {
  const resolved = normalize(join(dirname(componentFile), relativeUrl));
  if (!existsSync(resolved)) {
    errors.push({
      file: componentFile,
      reason: `${kind} file '${relativeUrl}' does not exist`,
    });
    return null;
  }
  return resolved;
}

function formatCounts(counts) {
  return `ts=${counts.typeScript}, template=${counts.template}, styles=${counts.styles}, total=${counts.total}`;
}

const files = execSync(`git ls-files --cached --others --exclude-standard ${roots.join(' ')}`, { encoding: 'utf8' })
  .split('\n')
  .filter(existsSync)
  .filter(file => file.endsWith('.ts') && !file.endsWith('.spec.ts') && !file.endsWith('.d.ts'));

for (const file of files) {
  const text = readFileSync(file, 'utf8');
  if (!/^\s*@Component\s*\(/m.test(text)) continue;
  const metadata = componentMetadataText(text);

  if (/\btemplate\s*:/.test(metadata)) {
    errors.push({
      file,
      reason: 'inline component templates are not allowed; use templateUrl',
    });
  }

  if (/\bstyles?\s*:/.test(metadata)) {
    errors.push({
      file,
      reason: 'inline component styles are not allowed; use styleUrl or styleUrls',
    });
  }

  const templateUrl = firstMetadataUrl(metadata, 'templateUrl');
  if (!templateUrl) {
    errors.push({
      file,
      reason: 'component must use an external templateUrl',
    });
  }

  const templateFile = templateUrl ? resolveSidecar(file, templateUrl, 'template') : null;
  const styleFiles = styleMetadataUrls(metadata)
    .map(styleUrl => resolveSidecar(file, styleUrl, 'style'))
    .filter(Boolean);

  const counts = {
    typeScript: lineCount(file),
    template: templateFile ? lineCount(templateFile) : 0,
    styles: styleFiles.reduce((sum, styleFile) => sum + lineCount(styleFile), 0),
    total: 0,
  };
  counts.total = counts.typeScript + counts.template + counts.styles;
  scanned.set(normalizePath(file), counts);

  const baselineEntry = baselineComponents[normalizePath(file)] ?? {};
  for (const [key, limit] of Object.entries(limits)) {
    const allowed = Math.max(limit, baselineEntry[key] ?? 0);
    if (counts[key] > allowed) {
      errors.push({
        file,
        reason:
          `${key} size ${counts[key]} exceeds limit ${limit}` +
          (baselineEntry[key] ? ` and baseline ${baselineEntry[key]}` : ''),
      });
    }
  }
}

for (const file of Object.keys(baselineComponents)) {
  if (!scanned.has(file)) {
    errors.push({
      file,
      reason: 'stale component-size baseline entry; remove it if the component was deleted',
    });
  }
}

if (errors.length > 0) {
  console.error(`\nFound ${errors.length} component-size violation(s):\n`);
  for (const error of errors) {
    const counts = scanned.get(normalizePath(error.file));
    console.error(`  - ${error.file}`);
    console.error(`    ${error.reason}`);
    if (counts) console.error(`    current: ${formatCounts(counts)}`);
  }
  console.error('\nRule: new/small components must stay within the global budgets in');
  console.error('scripts/component-size-baseline.json. Existing oversized components');
  console.error('are baseline debt and may not grow. Split templates/controllers/styles');
  console.error('before raising a baseline.\n');
  process.exit(1);
}

console.log(`OK: scanned ${scanned.size} Angular components; component size budgets hold.`);

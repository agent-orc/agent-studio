#!/usr/bin/env node
/**
 * Enforce folder-per-component layout under `src/app/`.
 *
 * Hard rule: a folder must contain at most one Angular component
 * (`.ts` file declaring `@Component`). Each component lives in its own
 * folder alongside its `.html` / `.scss` files.
 *
 * Hard rule: the containing folder's basename should match the
 * component file's basename (minus the optional `.component` suffix).
 * Existing descriptor-style names stay as explicit baseline exceptions;
 * new mismatches fail the lint gate.
 *
 * Examples that PASS:
 *   src/app/components/tooltip/tooltip.directive.ts          (utility, no @Component)
 *   src/app/features/cli/components/cli-console/cli-console.ts
 *   src/app/features/cli/components/cli-console/cli-console.component.ts
 *
 * Example that FAILS:
 *   src/app/features/cli/components/multi/a.component.ts
 *   src/app/features/cli/components/multi/b.component.ts     (two components in one folder)
 */
import { existsSync, readFileSync } from 'node:fs';
import { basename, dirname } from 'node:path';
import { execSync } from 'node:child_process';

const root = 'src/app projects/studio-ui/src projects/studio-ui-catalogue/src';
const files = execSync(`git ls-files --cached --others --exclude-standard ${root}`, { encoding: 'utf8' })
  .split('\n')
  .filter(existsSync)
  .filter(f => f.endsWith('.ts') && !f.endsWith('.spec.ts') && !f.endsWith('.d.ts'));

const errors = [];
const warnings = [];
const byDir = new Map();
const allowedFolderNameMismatches = new Set([
  'src/app/components/aspect-findings/aspect-findings-list.component.ts',
  'src/app/features/project-detail/components/project-observability/project-observability-panel.component.ts',
  'src/app/features/project-detail/components/project-product-runtime/project-product-runtime-panel.component.ts',
]);

for (const file of files) {
  const text = readFileSync(file, 'utf8');
  if (!/^\s*@Component\s*\(/m.test(text)) continue;

  const compName = basename(file).replace(/\.ts$/, '').replace(/\.component$/, '');
  const dir = dirname(file);
  const dirName = basename(dir);

  if (dirName !== compName && allowedFolderNameMismatches.has(file)) {
    warnings.push({
      file,
      reason: `folder '${dirName}/' does not match component name '${compName}' (baseline exception)`,
    });
  } else if (dirName !== compName) {
    errors.push({
      file,
      reason: `folder '${dirName}/' does not match component name '${compName}'`,
    });
  }

  if (!byDir.has(dir)) byDir.set(dir, []);
  byDir.get(dir).push({ file, compName });
}

for (const [dir, entries] of byDir) {
  if (entries.length > 1) {
    errors.push({
      file: dir,
      reason: `folder contains ${entries.length} component files: ${entries.map(e => e.compName).join(', ')} — split each into its own subfolder`,
    });
  }
}

if (warnings.length > 0) {
  console.warn(`\n${warnings.length} folder-name baseline exception(s) (non-blocking):\n`);
  for (const w of warnings) {
    console.warn(`  - ${w.file}`);
    console.warn(`    ${w.reason}`);
  }
}

if (errors.length > 0) {
  console.error(`\nFound ${errors.length} component-folder violation(s):\n`);
  for (const v of errors) {
    console.error(`  - ${v.file}`);
    console.error(`    ${v.reason}`);
  }
  console.error('\nRule: every Angular component (.ts file with @Component) must live in its');
  console.error('own matching folder alongside its .html and .scss files. No two components per folder.');
  console.error('See AGENTS.md (Frontend / folder-per-component) for details.\n');
  process.exit(1);
}

console.log(`OK: scanned ${files.length} .ts files; no two-components-per-folder violations.`);
if (warnings.length > 0) {
  console.log(`(${warnings.length} folder-name baseline exception(s) above — remove after renaming.)`);
}

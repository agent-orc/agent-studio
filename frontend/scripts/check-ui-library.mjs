import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { files, root } from './style-inventory.mjs';
import { apiReport } from './ui-api-report.mjs';

export function forbiddenImport(specifier, file, libraryRoot) {
  if (specifier.startsWith('.')) {
    const target = relative(libraryRoot, resolve(dirname(file), specifier));
    return target.startsWith('..');
  }
  return !/^(?:@angular\/|rxjs(?:\/|$)|vitest(?:\/|$))/.test(specifier);
}
export function missingInputs(required, states) {
  return states.flatMap(state => required.filter(input => !Object.hasOwn(state.inputs, input)).map(input => `${state.id}: missing required input ${input}`));
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const library = join(root, 'projects/studio-ui/src');
  const failures = [];
  for (const path of files(library).filter(path => path.endsWith('.ts'))) {
    const text = readFileSync(path, 'utf8');
    const visit = node => {
      const specifier = ts.isImportDeclaration(node) || ts.isExportDeclaration(node) ? node.moduleSpecifier
        : ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword ? node.arguments[0] : null;
      if (specifier && ts.isStringLiteral(specifier) && forbiddenImport(specifier.text, path, library)) {
        failures.push(`${relative(root, path)} imports outside studio-ui: ${specifier.text}`);
      }
      ts.forEachChild(node, visit);
    };
    visit(ts.createSourceFile(path, text, ts.ScriptTarget.Latest, true));
    if (!path.endsWith('.catalogue.ts')) continue;
    const start = text.indexOf('{\n', text.includes('...') ? text.indexOf('...') : text.indexOf(' = '));
    const end = text.indexOf('\n}', start) + 2;
    const entry = JSON.parse(text.slice(start, end));
    if (entry.component === null) continue;
    const controller = files(dirname(path)).filter(file => dirname(file) === dirname(path) && file.endsWith('.ts') && !file.includes('.spec.') && !file.includes('.catalogue.'))
      .map(file => readFileSync(file, 'utf8')).find(text => /@Component\(/.test(text));
    if (!controller) { failures.push(`${entry.id}: no component controller`); continue; }
    const required = [...controller.matchAll(/readonly\s+(\w+)\s*=\s*input\.required/g)].map(match => match[1]);
    failures.push(...missingInputs(required, entry.states).map(message => `${entry.id}/${message}`));
    if (!entry.documentation.includes('Example')) failures.push(`${entry.id}: missing usage example`);
  }
  const generated = apiReport(root);
  const reportPath = join(library, '../api-report.md');
  if (process.argv.includes('--write-api')) writeFileSync(reportPath, generated);
  else if (readFileSync(reportPath, 'utf8') !== generated) failures.push('api-report.md does not match the public TypeScript declarations');
  if (failures.length) { console.error(failures.join('\n')); process.exitCode = 1; }
  else console.log('Studio UI: import boundary, required catalogue inputs, usage and API export report pass.');
}

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = process.argv[2]
  ? path.resolve(process.argv[2])
  : path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src/app');
const localeMethods = new Set(['toLocaleString', 'toLocaleDateString', 'toLocaleTimeString']);
const intlConstructors = new Set(['NumberFormat', 'DateTimeFormat', 'RelativeTimeFormat', 'ListFormat', 'PluralRules', 'Collator', 'DisplayNames', 'Segmenter', 'DurationFormat']);
const failures = [];

function inspect(file) {
  const source = fs.readFileSync(file, 'utf8');
  const tree = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  function visit(node) {
    if ((ts.isCallExpression(node) || ts.isNewExpression(node)) && ts.isPropertyAccessExpression(node.expression)) {
      const target = node.expression;
      const isLocaleMethod = localeMethods.has(target.name.text);
      const isIntlConstructor = ts.isIdentifier(target.expression) && target.expression.text === 'Intl' && intlConstructors.has(target.name.text);
      if (isLocaleMethod || isIntlConstructor) {
        const first = node.arguments?.[0];
        const missing = !first ||
          (ts.isIdentifier(first) && first.text === 'undefined') ||
          (ts.isArrayLiteralExpression(first) && first.elements.length === 0);
        if (missing) {
          const line = tree.getLineAndCharacterOfPosition(node.getStart(tree)).line + 1;
          failures.push(`${path.relative(root, file)}:${line}: pass an explicit locale to ${target.name.text}`);
        }
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(tree);
}

function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const file = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(file);
    else if (file.endsWith('.ts') && !file.endsWith('.spec.ts')) inspect(file);
  }
}

walk(root);
if (failures.length) {
  console.error(failures.join('\n'));
  process.exitCode = 1;
} else {
  console.log('Product locale calls all specify a locale.');
}

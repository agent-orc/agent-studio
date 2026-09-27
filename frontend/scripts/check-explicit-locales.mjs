import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const localeMethods = new Set(['toLocaleString', 'toLocaleDateString', 'toLocaleTimeString']);
const intlConstructors = new Set(['NumberFormat', 'DateTimeFormat', 'RelativeTimeFormat', 'ListFormat', 'PluralRules', 'Collator']);

function withoutTypeWrapper(node) {
  while (ts.isParenthesizedExpression(node) || ts.isAsExpression(node) || ts.isTypeAssertionExpression(node)) {
    node = node.expression;
  }
  return node;
}

function lacksLocale(args) {
  if (!args?.length) return true;
  const first = withoutTypeWrapper(args[0]);
  return (ts.isIdentifier(first) && first.text === 'undefined') ||
    (ts.isArrayLiteralExpression(first) && first.elements.length === 0) ||
    (ts.isVoidExpression(first) && ts.isNumericLiteral(first.expression) && first.expression.text === '0');
}

export function findLocaleLessCalls(sourceText, fileName) {
  const source = ts.createSourceFile(fileName, sourceText, ts.ScriptTarget.Latest, true);
  const findings = [];
  function visit(node) {
    if (ts.isCallExpression(node) || ts.isNewExpression(node)) {
      const callee = node.expression;
      if (ts.isPropertyAccessExpression(callee)) {
        const localeMethod = ts.isCallExpression(node) && localeMethods.has(callee.name.text);
        const intlConstructor = ts.isIdentifier(callee.expression) && callee.expression.text === 'Intl' && intlConstructors.has(callee.name.text);
        if ((localeMethod || intlConstructor) && lacksLocale(node.arguments)) {
          const { line, character } = source.getLineAndCharacterOfPosition(node.getStart(source));
          findings.push(`${fileName}:${line + 1}:${character + 1}: ${callee.name.text} requires an explicit locale`);
        }
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(source);
  return findings;
}

function* productFiles(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const file = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* productFiles(file);
    else if (file.endsWith('.ts') && !file.endsWith('.spec.ts')) yield file;
  }
}

const scriptPath = fileURLToPath(import.meta.url);
if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  const frontendDir = path.resolve(path.dirname(scriptPath), '..');
  const findings = [...productFiles(path.join(frontendDir, 'src/app'))].flatMap(file =>
    findLocaleLessCalls(readFileSync(file, 'utf8'), path.relative(frontendDir, file)));
  if (findings.length) {
    console.error(findings.join('\n'));
    process.exitCode = 1;
  } else {
    console.log('Product locale calls all specify a locale.');
  }
}

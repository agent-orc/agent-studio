import { join, relative } from 'node:path';
import ts from 'typescript';

// Emit declarations in memory. This tracks input, output and method types,
// rather than only noticing a changed list of barrel exports.
export function apiReport(frontendRoot) {
  const library = join(frontendRoot, 'projects/studio-ui/src');
  const config = ts.readConfigFile(join(frontendRoot, 'tsconfig.json'), ts.sys.readFile);
  if (config.error) throw new Error(ts.flattenDiagnosticMessageText(config.error.messageText, '\n'));
  const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, frontendRoot);
  const program = ts.createProgram([join(library, 'public-api.ts')], {
    ...parsed.options, noEmit: false, declaration: true, emitDeclarationOnly: true,
    types: [], rootDir: library, outDir: join(frontendRoot, 'out-tsc/ui-api'), newLine: ts.NewLineKind.LineFeed,
  });
  const declarations = [];
  const result = program.emit(undefined, (_file, text, _bom, _error, sources) => {
    const source = sources?.[0];
    if (source?.fileName.startsWith(library)) {
      declarations.push([relative(library, source.fileName).replaceAll('\\', '/').replace(/\.ts$/, '.d.ts'), text]);
    }
  }, undefined, true);
  if (result.emitSkipped) throw new Error(result.diagnostics.map(d => ts.flattenDiagnosticMessageText(d.messageText, '\n')).join('\n'));
  let report = '# Public API\n\nGenerated TypeScript declarations for the source API and its public dependencies. Regenerate with `node scripts/check-ui-library.mjs --write-api`.\n';
  for (const [file, text] of declarations.sort((a, b) => a[0].localeCompare(b[0], 'en'))) {
    report += `\n## ${file}\n\n\`\`\`ts\n${text}\`\`\`\n`;
  }
  return report;
}

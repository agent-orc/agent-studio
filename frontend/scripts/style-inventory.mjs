import { existsSync, readdirSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import scss from 'postcss-scss';
import valueParser from 'postcss-value-parser';
import namedColors from 'color-name';
import ts from 'typescript';

export const root = process.env.STYLE_INVENTORY_ROOT ?? resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const tokenSource = 'projects/studio-ui/src/styles/';
export function files(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
    if (['node_modules', 'dist', '.angular', 'test-results'].includes(entry.name)) return [];
    const path = join(dir, entry.name);
    return entry.isDirectory() ? files(path) : [path];
  }).sort();
}
export const sourceFiles = () => ['src', 'projects', 'public'].flatMap(dir => files(join(root, dir)));
export const isToken = file => file.startsWith(tokenSource) && /_tokens-[^/]+\.scss$/.test(file)
  || /^src\/styles\/_tokens-(primitives|semantic)\.scss$/.test(file);
const dimension = /^-?(?:\d*\.)?\d+(?:px|r?em|%|[sld]?(?:vh|vw|vmin|vmax)|ch|ex|pt|cm|mm|lh|rlh|cap|ic|cq[whib]|cqmin|cqmax)$/i;
const spacing = /^(?:padding|margin|gap|row-gap|column-gap)(?:-|$)/;
const radius = /radius$/;
const motion = /^(?:transition|animation)(?:-|$)/;
const colorFunctions = /^(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)$/i;

// PostCSS supplies declarations and selectors, so comments and selector hexes
// cannot become findings. Each literal occurrence is counted, including fallbacks.
export function literals(property, value) {
  const out = [];
  const parsed = valueParser(value);
  parsed.walk(node => {
    if (node.type === 'function' && colorFunctions.test(node.value)) {
      out.push({ category: 'colour', literal: valueParser.stringify(node) });
      return false;
    }
    if (node.type !== 'word') return;
    const word = node.value;
    if (/^#[\da-f]{3,8}$/i.test(word) || Object.hasOwn(namedColors, word.toLowerCase())) {
      out.push({ category: 'colour', literal: word });
    } else if ((property === 'font-size' || property === 'font') && (dimension.test(word) || word === '0' || /^(?:xx-small|x-small|small|medium|large|x-large|xx-large|xxx-large)$/.test(word))) {
      out.push({ category: 'fontSize', literal: word });
    } else if (spacing.test(property) && (dimension.test(word) || word === '0')) {
      out.push({ category: 'spacing', literal: word });
    } else if (radius.test(property) && (dimension.test(word) || word === '0')) {
      out.push({ category: 'radius', literal: word });
    } else if (motion.test(property) && /^(?:\d*\.)?\d+m?s$/.test(word)) {
      out.push({ category: 'motion', literal: word });
    }
  });
  if (/^(box|text)-shadow$/.test(property) && /(?:\d(?:px|rem)|#[\da-f]|rgba?\()/i.test(value)) {
    out.push({ category: 'shadow', literal: value });
  }
  return out;
}

export function stylesheetFindings(file, text) {
  const out = [];
  const ast = scss.parse(text, { from: file });
  ast.walkDecls(decl => {
    for (const item of literals(decl.prop, decl.value)) out.push({
      file, selector: decl.parent.selector ?? decl.parent.name ?? ':root',
      property: decl.prop, value: decl.value, line: decl.source.start.line, ...item,
    });
  });
  return out;
}

export function boundStyleFindings(file, text) {
  const out = [];
  const add = (property, value, line) => {
    for (const item of literals(property, value)) out.push({ file, selector: 'inline binding', property, value, line, ...item });
  };
  // Literal style bindings are styles too. Variable expressions stay with their
  // data source; this scanner never guesses a colour from arbitrary UI copy.
  for (const match of text.matchAll(/\[style\.([\w-]+)(?:\.(px|rem|em))?\]="([^"]+)"/g)) {
    const [, property, unit, expression] = match;
    const line = text.slice(0, match.index).split('\n').length;
    if (unit && /^\d+(?:\.\d+)?$/.test(expression)) add(property, expression + unit, line);
    else for (const literal of expression.matchAll(/'([^']+)'/g)) add(property, literal[1], line);
  }
  for (const match of text.matchAll(/\.style\.(color|backgroundColor|fontSize)\s*=\s*['"]([^'"]+)['"]/g)) {
    const property = { color: 'color', backgroundColor: 'background-color', fontSize: 'font-size' }[match[1]];
    add(property, match[2], text.slice(0, match.index).split('\n').length);
  }
  return out;
}

export function controlFamilies(tag, attrs) {
  const classes = attrs.match(/\bclass="([^"]+)"/)?.[1] ?? '';
  return Object.entries({
    buttons: tag === 'button' || tag === 'a' && /btn|button/.test(classes),
    chips: /chip|badge|status-indicator/.test(tag + classes),
    tablesAndGrids: /^(?:table|app-data-grid|sui-data-grid)$/.test(tag) || /role="(?:table|grid)"/.test(attrs),
    menus: /menu/.test(tag) || /role="menu"/.test(attrs),
    dialogs: /dialog/.test(tag) || /role="(?:alert)?dialog"/.test(attrs),
    formFields: /^(?:input|select|textarea|app-field|sui-field)$/.test(tag),
    tabs: /tabs|segmented-control/.test(tag) || /(?:^|[-_\s])tabs?(?:$|[-_\s])/.test(classes) || /role="tab(?:list)?"/.test(attrs),
  }).filter(([, matches]) => matches).map(([family]) => family);
}

export function componentStyleFindings(file, text) {
  if (!/\b(?:styles|template)\s*:/.test(text)) return [];
  const out = [];
  const ast = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
  const literal = node => ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node);
  function visit(node) {
    if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'Component') {
      const metadata = node.arguments[0];
      if (metadata && ts.isObjectLiteralExpression(metadata)) {
        for (const property of metadata.properties) {
          if (!ts.isPropertyAssignment(property)) continue;
          const name = property.name.getText(ast).replaceAll(/["']/g, '');
          const value = property.initializer;
          if (name === 'styles') {
            const values = ts.isArrayLiteralExpression(value) ? value.elements : [value];
            for (const style of values.filter(literal)) out.push(...stylesheetFindings(file, style.text));
          } else if (name === 'template' && literal(value)) {
            for (const match of value.text.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)) out.push(...stylesheetFindings(file, match[1]));
            for (const match of value.text.matchAll(/\bstyle="([^"]*)"/g)) out.push(...stylesheetFindings(file, `inline { ${match[1]} }`));
          }
        }
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(ast);
  return out;
}

export function inventory() {
  const findings = [], implementations = {}, selectors = new Map();
  const styles = [], inline = [];
  for (const path of sourceFiles()) {
    const file = relative(root, path).replaceAll('\\', '/');
    if (!/\.(?:scss|css|html|ts)$/.test(file) || /\.spec\./.test(file)) continue;
    const text = readFileSync(path, 'utf8');
    if (/\.(html|ts)$/.test(file)) inline.push(...boundStyleFindings(file, text));
    if (/\.ts$/.test(file)) inline.push(...componentStyleFindings(file, text));
    if (/\.(?:scss|css)$/.test(file)) {
      if (isToken(file)) continue;
      styles.push(file);
      findings.push(...stylesheetFindings(file, text));
      scss.parse(text).walkRules(rule => {
        for (const selector of rule.selector.split(',')) {
          const key = selector.trim();
          if (!selectors.has(key)) selectors.set(key, new Set());
          selectors.get(key).add(file);
        }
      });
    }
    if (/\.html$/.test(file)) {
      for (const match of text.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)) {
        inline.push(...stylesheetFindings(file, match[1]));
      }
      for (const match of text.matchAll(/<([\w-]+)\b([^<>]*)>/g)) {
        const [, tag, attrs] = match;
        for (const family of controlFamilies(tag, attrs)) {
          const recipe = `${tag}.${attrs.match(/\bclass="([^"]+)"/)?.[1]?.split(/\s/)[0] ?? '(default)'}`;
          implementations[family] ??= {};
          implementations[family][recipe] ??= {};
          implementations[family][recipe][file] = (implementations[family][recipe][file] ?? 0) + 1;
        }
      }
      for (const match of text.matchAll(/\bstyle="([^"]*)"/g)) {
        const rows = stylesheetFindings(file, `inline { ${match[1]} }`);
        inline.push(...rows);
      }
    }
  }
  findings.push(...inline);
  const categories = ['colour', 'fontSize', 'spacing', 'radius', 'shadow', 'motion'];
  const counts = rows => Object.fromEntries(categories.map(category => [category, rows.filter(row => row.category === category).length]));
  const pages = {};
  for (const [selector, paths] of selectors) {
    if (paths.size !== 1) continue;
    const file = [...paths][0], feature = file.match(/^src\/app\/features\/([^/]+)/)?.[1];
    if (feature) { pages[feature] ??= { oneOffSelectors: 0, files: {} }; pages[feature].oneOffSelectors++; pages[feature].files[file] = (pages[feature].files[file] ?? 0) + 1; }
  }
  return {
    method: 'Authored src, projects and public styles, excluding dependency and build output. Declaration literals (including unitless zero; comments excluded), inline style attributes, embedded style blocks and literal style bindings. Component implementations are tag + first class recipes, not claims of behavioral equivalence. One-off selectors occur in exactly one stylesheet; pages are feature folders. Prototypes are inventoried too.',
    totals: counts(findings), stylesheets: styles.length,
    perFile: Object.fromEntries([...new Set(findings.map(row => row.file))].sort().map(file => [file, counts(findings.filter(row => row.file === file))])),
    findings, implementations,
    pages: Object.entries(pages).sort((a, b) => b[1].oneOffSelectors - a[1].oneOffSelectors).map(([page, data]) => ({ page, ...data })),
  };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const out = process.argv[2] ?? (process.env.JOB_RESULTS_DIR && join(process.env.JOB_RESULTS_DIR, 'inventory.json'));
  if (!out) throw new Error('Pass an output path or set JOB_RESULTS_DIR.');
  const report = inventory();
  mkdirSync(dirname(resolve(out)), { recursive: true });
  writeFileSync(out, JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify({ output: out, totals: report.totals, pages: report.pages.length }));
}

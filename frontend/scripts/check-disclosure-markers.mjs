#!/usr/bin/env node
/**
 * One disclosure grammar (AGT-2813, guideline rule ADM-17).
 *
 * The operator could not see that the result-header status line expands: the
 * only hint was a caret-sized glyph at the end of the sentence, and the product
 * compensated with a second control ("Why this status?") that did the same
 * thing. Across the app the same interaction was drawn three different ways.
 *
 * This gate keeps the grammar from drifting again. It scans every Angular
 * template for elements that carry `aria-expanded` - the wire signal of an
 * in-place disclosure - and requires the shared marker on them:
 *
 *   <app-disclosure-marker [open]="..."/>   (or the class it renders,
 *   `studio-disclosure__marker`, for a surface that mounts it differently)
 *
 * Every template that owns at least one disclosure site must be classified in
 * `disclosure-baseline.json`:
 *
 *   "enforced"   - the file follows ADM-17; every site in it must carry the
 *                  marker. Adding a bare toggle here fails the build.
 *   "documented" - the file legitimately diverges; the entry records WHY, and
 *                  the same reason is written up in the guideline. Kept small
 *                  and deliberate: an entry is an admission, not a default.
 *
 * A template that is in neither list fails as "unclassified", so a newly added
 * disclosure cannot slip in unnoticed - the author has to either adopt the
 * marker or state a reason.
 *
 * Run via `npm run lint:structure`.
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

// `--root` / `--baseline` exist so the self-test
// (check-disclosure-markers.test.mjs) can run the real checker over a fixture
// tree. Production runs take the defaults.
const args = process.argv.slice(2);
function arg(name, fallback) {
  const at = args.indexOf(`--${name}`);
  return at === -1 ? fallback : args[at + 1];
}
const ROOT = arg('root', 'src');
const BASELINE = arg('baseline', 'scripts/disclosure-baseline.json');
const MARKER_TAG = '<app-disclosure-marker';
const MARKER_CLASS = 'studio-disclosure__marker';

/** Elements that never contain children, so their subtree is the tag itself. */
const VOID_TAGS = new Set([
  'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input',
  'link', 'meta', 'param', 'source', 'track', 'wbr',
]);

function templates(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      if (entry === 'node_modules' || entry === 'mockups') continue;
      templates(full, out);
    } else if (entry.endsWith('.html')) {
      out.push(full);
    }
  }
  return out;
}

/** Start index of the element whose open tag contains `at`. */
function openTagStart(source, at) {
  return source.lastIndexOf('<', at);
}

function tagNameAt(source, start) {
  const m = /^<([a-zA-Z][\w-]*)/.exec(source.slice(start, start + 64));
  return m ? m[1] : null;
}

/** The element's full markup, open tag through matching close tag. */
function subtree(source, start) {
  const tag = tagNameAt(source, start);
  if (!tag) return '';
  const openEnd = source.indexOf('>', start);
  if (openEnd === -1) return source.slice(start);
  const selfClosing = source[openEnd - 1] === '/';
  if (selfClosing || VOID_TAGS.has(tag.toLowerCase())) return source.slice(start, openEnd + 1);

  const open = new RegExp(`<${tag}(?=[\\s/>])`, 'g');
  const close = new RegExp(`</${tag}\\s*>`, 'g');
  let depth = 1;
  let cursor = openEnd + 1;
  while (depth > 0) {
    open.lastIndex = cursor;
    close.lastIndex = cursor;
    const nextOpen = open.exec(source);
    const nextClose = close.exec(source);
    if (!nextClose) return source.slice(start);
    if (nextOpen && nextOpen.index < nextClose.index) {
      depth += 1;
      cursor = nextOpen.index + nextOpen[0].length;
      continue;
    }
    depth -= 1;
    cursor = nextClose.index + nextClose[0].length;
  }
  return source.slice(start, cursor);
}

function lineOf(source, index) {
  return source.slice(0, index).split('\n').length;
}

/** Comments are stripped index-preserving so prose about the rule never counts. */
function withoutComments(raw) {
  return raw.replace(/<!--[\s\S]*?-->/g, (m) => ' '.repeat(m.length));
}

/** Every `aria-expanded` site in one template, with its compliance verdict. */
function sitesIn(file) {
  const source = withoutComments(readFileSync(file, 'utf8'));
  const sites = [];
  const attr = /\[?attr\.aria-expanded\]?|\baria-expanded\b/g;
  let m;
  while ((m = attr.exec(source)) !== null) {
    const start = openTagStart(source, m.index);
    if (start === -1) continue;
    // A bound attribute written as `[attr.aria-expanded]` matches twice
    // through the alternation; collapse duplicates on the same element.
    if (sites.length > 0 && sites[sites.length - 1].start === start) continue;
    const markup = subtree(source, start);
    sites.push({
      start,
      line: lineOf(source, start),
      tag: tagNameAt(source, start),
      compliant: markup.includes(MARKER_TAG) || markup.includes(MARKER_CLASS),
    });
  }
  return sites;
}

/** The only reasons a template may stay outside the grammar. */
const DIVERGENCE_KINDS = new Set(['popup-trigger', 'tree-chevron', 'staged-adoption']);

const baseline = JSON.parse(readFileSync(BASELINE, 'utf8'));
const enforced = new Set(baseline.enforced ?? []);
const documented = baseline.documented ?? {};

for (const [rel, entry] of Object.entries(documented)) {
  if (!DIVERGENCE_KINDS.has(entry?.kind)) {
    console.error(`${BASELINE}: "${rel}" has kind "${entry?.kind}" - expected one of ${[...DIVERGENCE_KINDS].join(', ')}.`);
    process.exit(2);
  }
  if (!entry.reason || entry.reason.length < 20) {
    console.error(`${BASELINE}: "${rel}" needs a real reason, not "${entry.reason ?? ''}".`);
    process.exit(2);
  }
}

const failures = [];
let checkedFiles = 0;
let checkedSites = 0;

for (const file of templates(ROOT)) {
  const rel = relative('.', file).split(sep).join('/');
  const sites = sitesIn(file);
  if (sites.length === 0) {
    if (enforced.has(rel)) {
      failures.push(`${rel}: listed as enforced but has no disclosure site - drop the stale entry.`);
    } else if (documented[rel]) {
      failures.push(`${rel}: listed as documented but has no disclosure site - drop the stale entry.`);
    }
    continue;
  }
  checkedFiles += 1;
  checkedSites += sites.length;

  if (documented[rel]) continue;

  if (!enforced.has(rel)) {
    failures.push(
      `${rel}: ${sites.length} disclosure site(s) in an unclassified template. ` +
      `Adopt <app-disclosure-marker> and add the file to "enforced" in ${BASELINE}, ` +
      `or record why it cannot in "documented" (and in the guideline chapter ADM-17).`,
    );
    continue;
  }

  for (const site of sites.filter((s) => !s.compliant)) {
    failures.push(
      `${rel}:${site.line}: <${site.tag}> carries aria-expanded without the shared marker. ` +
      `Render <app-disclosure-marker [open]="..."> as its first child (ADM-17).`,
    );
  }
}

for (const rel of enforced) {
  if (!documented[rel]) continue;
  failures.push(`${rel}: listed as both enforced and documented - pick one.`);
}

if (failures.length > 0) {
  console.error('Disclosure grammar check FAILED (guideline rule ADM-17):\n');
  for (const f of failures) console.error(`  - ${f}`);
  console.error(`\n${failures.length} problem(s). See docs/operations/admin-design-guideline/index.html#disclosure-affordance`);
  process.exit(1);
}

console.log(
  `Disclosure grammar OK: ${checkedSites} site(s) in ${checkedFiles} template(s) ` +
  `(${enforced.size} enforced, ${Object.keys(documented).length} documented divergence(s)).`,
);

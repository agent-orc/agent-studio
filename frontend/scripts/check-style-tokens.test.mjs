import { test } from 'node:test';
import assert from 'node:assert/strict';
import { stylesheetFindings, literals, boundStyleFindings, controlFamilies, componentStyleFindings } from './style-inventory.mjs';
import { debt, check, checkHistory } from './check-style-tokens.mjs';

test('inventory ignores comments and selectors, finds functions, fallbacks, shorthand and named colours', () => {
  const findings = stylesheetFindings('example.scss', '// #abc\n#abc { color: rgb(1, 2, 3); border: 1px solid red; font: 600 12px sans-serif; gap: 4px; }');
  assert.equal(findings.filter(row => row.category === 'colour').length, 2);
  assert.equal(findings.filter(row => row.category === 'fontSize').length, 1);
  assert.equal(literals('font-size', 'var(--studio-type, 13px)').length, 1);
  assert.equal(literals('color', 'var(--studio-fg)').length, 0);
  assert.equal(literals('font-size', 'small').length, 1);
  assert.equal(boundStyleFindings('a.html', `<b [style.color]="'red'" [style.font-size.px]="12">`).length, 2);
  assert.equal(boundStyleFindings('a.ts', `element.style.fontSize = '12px';`).length, 1);
});
test('a committed baseline increase is rejected even when HEAD contains that increase', () => {
  const previous = { raw: { existing: 1 }, undefined: {} };
  const committed = { raw: { existing: 2 }, undefined: {} };
  assert.equal(checkHistory(committed, committed, [committed, previous]).length, 1);
  assert.deepEqual(checkHistory(previous, previous, [committed, previous]), []);
});
test('native tab buttons are inventoried in both families', () => {
  assert.deepEqual(controlFamilies('button', 'class="pane-tab" role="tab"'), ['buttons', 'tabs']);
  assert.deepEqual(controlFamilies('div', 'role="grid"'), ['tablesAndGrids']);
});
test('inline Angular styles and templates cannot bypass the token guard', () => {
  const rows = componentStyleFindings('inline.ts', "@Component({ styles: [`.a { color: red; font-size: 13px; }`], template: `<b style=\"color: blue\">Text</b>` }) class Example {}");
  assert.equal(rows.filter(row => row.category === 'colour').length, 2);
  assert.equal(rows.filter(row => row.category === 'fontSize').length, 1);
  assert.deepEqual(componentStyleFindings('a.ts', "// styles: ['color: red']"), []);
});
test('a new literal, a repeated occurrence and a grown baseline fail; removals pass', () => {
  const raw = debt({ findings: stylesheetFindings('a.scss', '.a { color: red; font-size: 13px; }') });
  const baseline = { raw, undefined: {} };
  assert.deepEqual(check(baseline, baseline, baseline), []);
  assert.deepEqual(check({ raw: {}, undefined: {} }, baseline, baseline), []);
  assert.equal(check({ raw: { ...raw, new: 1 }, undefined: {} }, baseline).length, 1);
  assert.equal(check({ raw: Object.fromEntries(Object.entries(raw).map(([key, count]) => [key, count + 1])), undefined: {} }, baseline).length, 2);
  assert.equal(check(baseline, baseline, { raw: {}, undefined: {} }).length, 2);
  assert.equal(check({ raw, undefined: { '--studio-missing': 1 } }, baseline).length, 1);
});

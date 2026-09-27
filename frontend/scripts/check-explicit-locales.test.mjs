import assert from 'node:assert/strict';
import { test } from 'node:test';
import { findLocaleLessCalls } from './check-explicit-locales.mjs';

test('rejects omitted and default locale arguments', () => {
  const source = `
    count.toLocaleString();
    date.toLocaleDateString(undefined, { year: 'numeric' });
    date.toLocaleTimeString([], { hour: '2-digit' });
    new Intl.NumberFormat().format(count);
    Intl.DateTimeFormat(undefined).format(date);
    new Intl.Collator(void 0);
  `;
  assert.equal(findLocaleLessCalls(source, 'example.ts').length, 6);
});

test('accepts explicit locales and configured locale variables', () => {
  const source = `
    count.toLocaleString('en-US');
    date.toLocaleDateString(locale, { year: 'numeric' });
    new Intl.NumberFormat('en-US').format(count);
    Intl.DateTimeFormat(locale).format(date);
  `;
  assert.deepEqual(findLocaleLessCalls(source, 'example.ts'), []);
});

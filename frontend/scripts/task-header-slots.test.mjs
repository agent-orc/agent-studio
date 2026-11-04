import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { parseTemplate } from '@angular/compiler';

const root = new URL('../src/app/features/task-detail/', import.meta.url);

test('task header children are registered once and stay in slot order', () => {
  const template = readFileSync(new URL('task-detail.html', root), 'utf8');
  const registry = readFileSync(new URL('task-header-slots.ts', root), 'utf8');
  const registered = [...registry.matchAll(/^  '(app-[\w-]+|detail__review-budget)',?$/gm)]
    .map(match => match[1]);
  const parsed = parseTemplate(template, 'task-detail.html');
  assert.equal(parsed.errors, null, 'task-detail template must parse');
  const detail = parsed.nodes.find(node => node.name === 'div'
    && node.attributes?.some(attribute => attribute.name === 'class' && attribute.value === 'detail'));
  assert.ok(detail, 'task detail root must exist');
  const panesIndex = detail.children.findIndex(node => node.name === 'div'
    && node.attributes?.some(attribute => attribute.name === 'class'
      && attribute.value.split(/\s+/).includes('detail__panes')));
  assert.ok(panesIndex > 0, 'pane boundary must exist');

  function slots(nodes) {
    return nodes.flatMap(node => {
      if (node.name) {
        const marker = node.attributes?.find(attribute => attribute.name === 'class')?.value;
        return [marker?.split(/\s+/).includes('detail__review-budget')
          ? 'detail__review-budget' : node.name];
      }
      if (node.branches) return node.branches.flatMap(branch => slots(branch.children));
      if (node.children) return slots(node.children);
      return [];
    });
  }
  const rendered = slots(detail.children.slice(0, panesIndex));

  assert.ok(registered.length > 0, 'slot registry must contain task header slots');
  assert.equal(new Set(registered).size, registered.length, 'registry contains duplicate slots');
  assert.deepEqual(rendered, registered,
    'register each child above the panes in its actual order');
});

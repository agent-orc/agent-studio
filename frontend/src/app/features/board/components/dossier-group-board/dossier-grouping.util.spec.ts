import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { groupCardsByDossier } from './dossier-grouping.util';

function task(id: string, path: string | null, state = '2-ready', projectName = 'Demo'): TaskInfo {
  return { id, taskKey: id, key: id, title: id, state, projectName,
    dossier: path ? { path } : null } as TaskInfo;
}

describe('groupCardsByDossier', () => {
  it('groups visible cards by primary Dossier and keeps unassigned cards separate', () => {
    const groups = groupCardsByDossier([
      task('a', 'docs/operations/one/index.html', '6-completed'),
      task('b', 'docs/operations/one/index.html'),
      task('c', null),
    ]);
    expect(groups.map(group => group.cards.map(card => card.id))).toEqual([['a', 'b'], ['c']]);
    expect(groups[0].completed).toBe(1);
    expect(groups[0].cards.length).toBe(2);
  });

  it('keeps the same repository path in different projects separate', () => {
    const groups = groupCardsByDossier([
      task('a', 'docs/operations/one/index.html', '2-ready', 'Alpha'),
      task('b', 'docs/operations/one/index.html', '2-ready', 'Beta'),
    ]);
    expect(groups).toHaveLength(2);
  });
});

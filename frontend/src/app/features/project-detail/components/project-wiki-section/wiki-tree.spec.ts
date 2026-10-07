import { describe, expect, it } from 'vitest';
import { WikiTreeNode } from '../../../../models/project-docs.model';
import {
  collectDirectDocumentNames,
  collectFolderIds,
  collectDocumentPaths,
  filterWikiTree,
  filterWikiTreeByTags,
  filterWikiSearchByTags,
  flattenWikiTree,
  nodeId,
  planWikiSiblingReorder,
  reorderWikiFiles,
} from './wiki-tree';

const file = (relPath: string, type: 'md' | 'html' | 'json' = 'md', title = relPath): WikiTreeNode => ({
  name: relPath.split('/').pop()!,
  title,
  relPath,
  type,
  children: [],
});

const folder = (relPath: string, children: WikiTreeNode[], title = relPath): WikiTreeNode => ({
  name: relPath.split('/').pop()!,
  title,
  relPath,
  type: 'folder',
  children,
});

describe('wiki tag filtering', () => {
  it('keeps matching pages with their parent folders and filters search hits to the same pages', () => {
    const tagged = { ...file('concepts/a.md'), tags: ['execution-and-runner', 'decision'] };
    const other = { ...file('concepts/b.md'), tags: ['decision'] };
    const roots = [folder('concepts', [tagged, other])];
    const filtered = filterWikiTreeByTags(roots, new Set(['execution-and-runner', 'decision']));
    expect(collectDocumentPaths(filtered)).toEqual(['concepts/a.md']);
    expect(roots[0].children).toHaveLength(2);
    const response = {
      query: 'concepts', semanticUsed: false, expandedTerms: [], durationMs: 1,
      results: [tagged, other].map(node => ({
        relPath: node.relPath!, title: node.title, kind: 'md', snippet: '', score: 1, updatedAt: null,
      })),
    };
    expect(filterWikiSearchByTags(response, filtered, new Set(['execution-and-runner']))?.results.map(hit => hit.relPath))
      .toEqual(['concepts/a.md']);
    expect(filterWikiTreeByTags(roots, new Set(['missing']))).toEqual([]);
  });
});

describe('flattenWikiTree', () => {
  it('lists folders collapsed and only descends into expanded ones', () => {
    const roots = [folder('concepts', [file('concepts/a.md', 'md', 'A')]), file('README.md', 'md', 'Index')];

    const collapsed = flattenWikiTree(roots, new Set());
    expect(collapsed.map(r => nodeId(r.node))).toEqual(['concepts', 'README.md']);
    const folderRow = collapsed[0];
    expect(folderRow.hasChildren).toBe(true);
    expect(folderRow.expanded).toBe(false);

    const expanded = flattenWikiTree(roots, new Set(['concepts']));
    expect(expanded.map(r => nodeId(r.node))).toEqual(['concepts', 'concepts/a.md', 'README.md']);
    expect(expanded[1].depth).toBe(1);
  });

  it('surfaces html and json doc nodes alongside markdown', () => {
    const roots = [folder('concepts', [
      file('concepts/page.html', 'html', 'Page'),
      file('concepts/page.metadata.json', 'json', 'Page metadata'),
    ])];
    const rows = flattenWikiTree(roots, new Set(['concepts']));
    const leaf = rows.find(r => r.node.relPath === 'concepts/page.html')!;
    expect(leaf.node.type).toBe('html');
    expect(rows.find(r => r.node.relPath === 'concepts/page.metadata.json')?.node.type).toBe('json');
  });
});

describe('filterWikiTree', () => {
  it('returns the tree unchanged for an empty needle', () => {
    const roots = [file('a.md'), file('b.md')];
    expect(filterWikiTree(roots, '   ').map(n => n.relPath)).toEqual(['a.md', 'b.md']);
  });

  it('keeps matching files and the folders that lead to them', () => {
    const roots = [
      folder('concepts', [file('concepts/overview.md', 'md', 'Concept overview'), file('concepts/misc.md', 'md', 'Misc')]),
      file('README.md', 'md', 'Docs index'),
    ];
    const filtered = filterWikiTree(roots, 'concept');
    // README drops out; the concepts folder is kept but only the matching child.
    expect(filtered.map(n => n.relPath)).toEqual(['concepts']);
    expect(filtered[0].children.map(c => c.relPath)).toEqual(['concepts/overview.md']);
  });

  it('keeps a folder when the folder name itself matches', () => {
    const roots = [folder('architecture', [file('architecture/x.md', 'md', 'X')])];
    const filtered = filterWikiTree(roots, 'architect');
    expect(filtered.map(n => n.relPath)).toEqual(['architecture']);
  });
});

describe('collectFolderIds', () => {
  it('returns every folder id including nested ones', () => {
    const roots = [
      folder('a', [folder('a/b', [file('a/b/c.md')])]),
      file('top.md'),
    ];
    expect(collectFolderIds(roots).sort()).toEqual(['a', 'a/b'].sort());
  });
});

describe('document order helpers', () => {
  it('collects persisted document order and reorders one parent immutably', () => {
    const roots = [
      folder('concepts', [file('concepts/a.md'), file('concepts/b.md'), file('concepts/c.md')]),
      file('README.md'),
    ];

    const reordered = reorderWikiFiles(roots, 'concepts', ['c.md', 'a.md']);

    expect(collectDocumentPaths(reordered)).toEqual([
      'concepts/c.md', 'concepts/a.md', 'concepts/b.md', 'README.md',
    ]);
    expect(collectDocumentPaths(roots)).toEqual([
      'concepts/a.md', 'concepts/b.md', 'concepts/c.md', 'README.md',
    ]);
  });

  it('plans same-parent file and folder reorders only', () => {
    const roots = [
      folder('concepts', [
        folder('concepts/alpha', [file('concepts/alpha/a.md')]),
        folder('concepts/beta', [file('concepts/beta/b.md')]),
        file('concepts/a.md'),
        file('concepts/b.md'),
        file('concepts/c.md'),
      ]),
    ];

    expect(collectDirectDocumentNames(roots, 'concepts')).toEqual(['a.md', 'b.md', 'c.md']);
    expect(planWikiSiblingReorder(
      roots, 'concepts/c.md', 'concepts/a.md', 'file',
    )).toEqual({
      parentRel: 'concepts',
      orderedNames: ['c.md', 'a.md', 'b.md'],
    });
    expect(planWikiSiblingReorder(
      roots, 'concepts/alpha', 'concepts/beta', 'folder',
    )).toEqual({
      parentRel: 'concepts',
      orderedNames: ['beta', 'alpha'],
    });
    expect(planWikiSiblingReorder(
      roots, 'concepts/a.md', 'concepts/alpha/a.md', 'file',
    )).toBeNull();
  });
});

import { describe, expect, it } from 'vitest';
import {
  wikiBasename,
  wikiDescribeError,
  wikiFormatTimestamp,
  wikiJoinRel,
  wikiParentDir,
  wikiSlug,
} from './wiki-path.util';

/**
 * AGT-2819 pulled these helpers out of `project-wiki-section.ts` so the section
 * and the extracted drift dialog stop keeping private copies.
 */
describe('wikiParentDir / wikiBasename / wikiJoinRel', () => {
  it('splits and rejoins a nested path', () => {
    expect(wikiParentDir('docs/system/model.md')).toBe('docs/system');
    expect(wikiBasename('docs/system/model.md')).toBe('model.md');
    expect(wikiJoinRel('docs/system', 'model.md')).toBe('docs/system/model.md');
  });

  it('treats a root-level entry as having no parent', () => {
    expect(wikiParentDir('README.md')).toBe('');
    expect(wikiBasename('README.md')).toBe('README.md');
    expect(wikiJoinRel('', 'README.md')).toBe('README.md');
  });
});

describe('wikiSlug', () => {
  it('reduces a path to a filesystem-safe slug', () => {
    expect(wikiSlug('docs/System Model.md')).toBe('docs-system-model-md');
  });

  it('never returns an empty slug', () => {
    expect(wikiSlug('   ')).toBe('document');
    expect(wikiSlug('///')).toBe('document');
  });
});

describe('wikiFormatTimestamp', () => {
  it('is blank for a missing stamp', () => {
    expect(wikiFormatTimestamp(null)).toBe('');
    expect(wikiFormatTimestamp(undefined)).toBe('');
    expect(wikiFormatTimestamp('')).toBe('');
  });

  it('returns unparseable input verbatim rather than "Invalid Date"', () => {
    expect(wikiFormatTimestamp('not-a-date')).toBe('not-a-date');
  });

  it('formats a real stamp', () => {
    expect(wikiFormatTimestamp('2026-09-14T08:00:00Z').length).toBeGreaterThan(0);
  });
});

describe('wikiDescribeError', () => {
  it('prefers the API error text, then the exception message, then the fallback', () => {
    expect(wikiDescribeError({ error: { error: 'Path is outside the wiki root' } }, 'fallback'))
      .toBe('Path is outside the wiki root');
    expect(wikiDescribeError({ message: 'Http failure' }, 'fallback')).toBe('Http failure');
    expect(wikiDescribeError({}, 'fallback')).toBe('fallback');
    expect(wikiDescribeError(null, 'fallback')).toBe('fallback');
  });
});

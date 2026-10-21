import { describe, expect, it } from 'vitest';
import {
  hashSegments,
  kvValueOf,
  routeSegmentOf,
  withKvSegment,
  withRouteSegment,
} from './url-hash.util';

describe('url-hash.util', () => {
  describe('hashSegments', () => {
    it('splits a composite hash and drops empty parts', () => {
      expect(hashSegments('#/workspace/settings&filters=a&&diff')).toEqual([
        '/workspace/settings', 'filters=a', 'diff',
      ]);
    });

    it('handles empty and bare hashes', () => {
      expect(hashSegments('')).toEqual([]);
      expect(hashSegments('#')).toEqual([]);
    });
  });

  describe('routeSegmentOf / kvValueOf', () => {
    it('finds the route segment regardless of position', () => {
      expect(routeSegmentOf('#filters=a&/workspace/settings')).toBe('/workspace/settings');
      expect(routeSegmentOf('#/board&filters=a')).toBe('/board');
      expect(routeSegmentOf('#filters=a')).toBeNull();
    });

    it('reads a kv value without decoding it', () => {
      expect(kvValueOf('#/board&filters=projects%3AAgent%20Studio', 'filters'))
        .toBe('projects%3AAgent%20Studio');
      expect(kvValueOf('#filters=a', 'filter')).toBeNull();
    });

    it('does not mistake a route query for a kv segment', () => {
      expect(kvValueOf('#/projects/x/wiki?page=a', 'page')).toBeNull();
    });
  });

  describe('withRouteSegment', () => {
    it('adds a route in front of existing kv segments', () => {
      expect(withRouteSegment('#filters=a', '/workspace/settings'))
        .toBe('#/workspace/settings&filters=a');
    });

    it('replaces a foreign route instead of stacking a second one', () => {
      expect(withRouteSegment('#/workspace/settings&filters=a', '/board'))
        .toBe('#/board&filters=a');
    });

    it('removes the route and keeps the rest', () => {
      expect(withRouteSegment('#/workspace/settings&filters=a&diff', null))
        .toBe('#filters=a&diff');
    });

    it('returns the empty string when nothing remains', () => {
      expect(withRouteSegment('#/workspace/settings', null)).toBe('');
    });
  });

  describe('withKvSegment', () => {
    it('inserts after the route and preserves unknown segments', () => {
      expect(withKvSegment('#/workspace/settings&diff', 'filters', 'a'))
        .toBe('#/workspace/settings&filters=a&diff');
    });

    it('replaces an existing value and drops legacy keys', () => {
      expect(withKvSegment('#filter=old&filters=old2&diff', 'filters', 'new', ['filter']))
        .toBe('#filters=new&diff');
    });

    it('removes the segment with a null value', () => {
      expect(withKvSegment('#/board&filters=a', 'filters', null)).toBe('#/board');
      expect(withKvSegment('#filters=a', 'filters', null)).toBe('');
    });
  });
});

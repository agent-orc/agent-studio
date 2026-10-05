import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, beforeEach } from 'vitest';
import { TagRegistryStore } from './tag-registry.store';

describe('TagRegistryStore workspace refresh', () => {
  let store: TagRegistryStore;
  let http: HttpTestingController;
  const shared = (label: string) => ({ id: 'shared', label, color: '#999', description: '', kind: 'facet' as const });
  const projectTag = { id: 'project-only', label: 'Project only', color: '#777', description: '', kind: 'facet' as const };

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    store = TestBed.inject(TagRegistryStore);
    http = TestBed.inject(HttpTestingController);
  });

  it('removes deleted workspace tags from loaded project views and refreshes project tags', () => {
    store.set([shared('Old')]);
    store.loadProject('Alpha');
    http.expectOne('/api/projects/Alpha/tags').flush({ items: [shared('Old'), projectTag] });
    expect(store.byIdForProject('Alpha').has('shared')).toBe(true);

    store.set([]);
    expect(store.byIdForProject('Alpha').has('shared')).toBe(false);
    expect(store.tags().some(tag => tag.id === 'shared')).toBe(false);
    expect(store.activeProjectLoaded()).toBe(false);
    http.expectOne('/api/projects/Alpha/tags').flush({ items: [projectTag] });
    expect(store.byIdForProject('Alpha').get('project-only')?.label).toBe('Project only');
    http.verify();
  });

  it('ignores an old in-flight project response after a workspace edit', () => {
    store.set([shared('Old')]);
    store.loadProject('Alpha');
    const oldRequest = http.expectOne('/api/projects/Alpha/tags');

    store.set([shared('New')]);
    expect(store.byIdForProject('Alpha').get('shared')?.label).toBe('New');
    expect(store.activeProjectLoaded()).toBe(false);

    http.expectOne('/api/projects/Alpha/tags').flush({ items: [shared('New'), projectTag] });
    oldRequest.flush({ items: [shared('Old'), projectTag] });
    expect(store.tags().find(tag => tag.id === 'shared')?.label).toBe('New');
    expect(store.activeProjectLoaded()).toBe(true);
    http.verify();
  });

  it('refreshes an inactive project cache used by chips', () => {
    store.set([shared('Old')]);
    store.ensureProject('Alpha');
    http.expectOne('/api/projects/Alpha/tags').flush({ items: [shared('Old'), projectTag] });
    store.loadProject(null);

    store.set([shared('New')]);
    expect(store.byIdForProject('Alpha').get('shared')?.label).toBe('New');
    http.expectOne('/api/projects/Alpha/tags').flush({ items: [shared('New'), projectTag] });
    expect(store.byIdForProject('Alpha').get('project-only')?.label).toBe('Project only');
    http.verify();
  });
});

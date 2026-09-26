import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TagFiltersComponent } from './tag-filters.component';
import { TagRegistryStore } from '../../services/tag-registry.store';
import { BoardFiltersService } from '../../features/board/state/board-filters.service';

describe('shared area and tag filters', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    TestBed.inject(TagRegistryStore).set([
      { id: 'execution-and-runner', label: 'Execution and runner', color: '#999', description: '', kind: 'area' },
      { id: 'decision', label: 'Decision', color: '#888', description: '', kind: 'facet' },
    ]);
  });

  it('writes one shared selection that both controls read', () => {
    const fixture = TestBed.createComponent(TagFiltersComponent);
    fixture.detectChanges();
    const controls = fixture.nativeElement.querySelectorAll('select') as NodeListOf<HTMLSelectElement>;
    controls[0].value = 'execution-and-runner';
    controls[0].dispatchEvent(new Event('change'));
    controls[1].value = 'decision';
    controls[1].dispatchEvent(new Event('change'));
    const selected = TestBed.inject(BoardFiltersService).activeTagFilter();
    expect([...selected]).toEqual(['execution-and-runner', 'decision']);
    controls[0].value = '';
    controls[0].dispatchEvent(new Event('change'));
    expect([...TestBed.inject(BoardFiltersService).activeTagFilter()]).toEqual(['decision']);
  });

  it('replaces project-specific options and removes an invalid selection on project change', () => {
    const fixture = TestBed.createComponent(TagFiltersComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.detectChanges();
    http.expectOne('/api/projects/Alpha/tags').flush({ items: [
      { id: 'alpha-only', label: 'Alpha only', color: '#777', description: '', kind: 'facet' },
    ] });
    fixture.detectChanges();
    const facet = fixture.nativeElement.querySelector('[data-testid="shared-facet-filter"]') as HTMLSelectElement;
    expect(facet.querySelector('option[value="alpha-only"]')).not.toBeNull();
    expect(TestBed.inject(TagRegistryStore).workspaceTags().some(tag => tag.id === 'alpha-only')).toBe(false);
    facet.value = 'alpha-only';
    facet.dispatchEvent(new Event('change'));
    expect([...TestBed.inject(BoardFiltersService).activeTagFilter()]).toContain('alpha-only');

    fixture.componentRef.setInput('projectName', 'Beta');
    fixture.detectChanges();
    expect(facet.querySelector('option[value="alpha-only"]')).toBeNull();
    http.expectOne('/api/projects/Beta/tags').flush({ items: [
      { id: 'beta-only', label: 'Beta only', color: '#666', description: '', kind: 'facet' },
    ] });
    fixture.detectChanges();
    expect(facet.querySelector('option[value="beta-only"]')).not.toBeNull();
    expect(TestBed.inject(BoardFiltersService).activeTagFilter().size).toBe(0);
    http.verify();
  });
});

import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { TagChipsComponent } from './tag-chips.component';
import { TagRegistryStore } from '../../services/tag-registry.store';

describe('project-scoped tag chips', () => {
  it('keeps each project label and color after the active filter project changes', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const store = TestBed.inject(TagRegistryStore);
    store.set([{ id: 'shared', label: 'Shared', color: '#999', description: '', kind: 'facet' }]);
    const http = TestBed.inject(HttpTestingController);
    const alpha = TestBed.createComponent(TagChipsComponent);
    alpha.componentRef.setInput('ids', ['project-tag']);
    alpha.componentRef.setInput('projectName', 'Alpha');
    alpha.detectChanges();
    const beta = TestBed.createComponent(TagChipsComponent);
    beta.componentRef.setInput('ids', ['project-tag']);
    beta.componentRef.setInput('projectName', 'Beta');
    beta.detectChanges();

    http.expectOne('/api/projects/Alpha/tags').flush({ items: [
      { id: 'project-tag', label: 'Alpha tag', color: '#111', description: 'Alpha', kind: 'facet' },
    ] });
    http.expectOne('/api/projects/Beta/tags').flush({ items: [
      { id: 'project-tag', label: 'Beta tag', color: '#222', description: 'Beta', kind: 'facet' },
    ] });
    store.loadProject('Beta');
    alpha.detectChanges();
    beta.detectChanges();
    const alphaChip = alpha.nativeElement.querySelector('[data-tag-id="project-tag"]') as HTMLElement;
    const betaChip = beta.nativeElement.querySelector('[data-tag-id="project-tag"]') as HTMLElement;
    expect(alphaChip.textContent).toContain('Alpha tag');
    expect(alphaChip.style.getPropertyValue('--tag-color')).toBe('#111');
    expect(betaChip.textContent).toContain('Beta tag');
    expect(betaChip.style.getPropertyValue('--tag-color')).toBe('#222');
    store.loadProject('Alpha');
    alpha.detectChanges();
    beta.detectChanges();
    expect(alphaChip.textContent).toContain('Alpha tag');
    expect(betaChip.textContent).toContain('Beta tag');
    http.verify();
  });
});

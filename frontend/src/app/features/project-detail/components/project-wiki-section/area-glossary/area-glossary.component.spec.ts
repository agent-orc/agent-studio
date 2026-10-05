import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AreaGlossaryComponent } from './area-glossary.component';
import { ProjectDocsService } from '../../../../../services/project-docs.service';
import { TaskService } from '../../../../../services/task.service';
import { TaskReferenceNavigationService } from '../../../../../services/task-reference-navigation.service';
import { WorkbenchListItem } from '../../../../../models/project-docs.model';

const area = (id: string) => ({ id, label: id, description: '', glossaryPath: `docs/areas/${id}/glossary.md` });
const glossary = (id: string, term: string) => ({ areaId: id, label: id, path: `docs/areas/${id}/glossary.md`,
  exists: true, terms: [{ term, definition: `${term} definition`, synonyms: [] }] });
const dossier = (id: string) => ({ id, title: id, tags: ['runner'] } as WorkbenchListItem);

describe('AreaGlossaryComponent request ownership', () => {
  let http: HttpTestingController;
  let catalogues: Map<string, Subject<{ items: WorkbenchListItem[] }>>;

  beforeEach(() => {
    catalogues = new Map(['Alpha', 'Beta'].map(project => [project, new Subject()]));
    TestBed.configureTestingModule({ providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: ProjectDocsService, useValue: { getWorkbenches: vi.fn((project: string) => catalogues.get(project)!) } },
      { provide: TaskService, useValue: { grouped: signal({}) } },
      { provide: TaskReferenceNavigationService, useValue: { openTaskKey: vi.fn() } },
    ] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    try { http.verify(); } finally { TestBed.resetTestingModule(); }
  });

  function mount() {
    const fixture = TestBed.createComponent(AreaGlossaryComponent);
    fixture.componentRef.setInput('projectName', 'Alpha');
    fixture.detectChanges();
    return fixture;
  }

  it('cancels the previous area request and renders only the latest selection', () => {
    const fixture = mount();
    http.expectOne('/api/projects/Alpha/areas').flush({ items: [area('runner'), area('gates')] });
    fixture.componentInstance.select('runner');
    fixture.detectChanges();
    const old = http.expectOne('/api/projects/Alpha/areas/runner/glossary');
    fixture.componentInstance.select('gates');
    fixture.detectChanges();
    const current = http.expectOne('/api/projects/Alpha/areas/gates/glossary');
    current.flush(glossary('gates', 'Gate'));
    fixture.detectChanges();
    expect(old.cancelled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Gate definition');
    expect(fixture.componentInstance.glossary()?.areaId).toBe('gates');
  });

  it('clears project state and cancels its requests when the project changes', () => {
    const fixture = mount();
    const oldAreas = http.expectOne('/api/projects/Alpha/areas');
    catalogues.get('Alpha')!.next({ items: [dossier('Alpha dossier')] });
    fixture.componentInstance.select('runner');
    fixture.detectChanges();
    const oldGlossary = http.expectOne('/api/projects/Alpha/areas/runner/glossary');
    fixture.componentRef.setInput('projectName', 'Beta');
    fixture.detectChanges();
    expect(fixture.componentInstance.areaId()).toBeNull();
    expect(fixture.componentInstance.glossary()).toBeNull();
    expect(fixture.componentInstance.dossiers()).toEqual([]);
    expect(oldAreas.cancelled).toBe(true);
    expect(oldGlossary.cancelled).toBe(true);
    http.expectOne('/api/projects/Beta/areas').flush({ items: [area('runner')] });
    catalogues.get('Beta')!.next({ items: [dossier('Beta dossier')] });
    catalogues.get('Alpha')!.next({ items: [dossier('Stale Alpha dossier')] });
    fixture.componentInstance.select('runner');
    fixture.detectChanges();
    http.expectOne('/api/projects/Beta/areas/runner/glossary').flush(glossary('runner', 'Beta runner'));
    fixture.detectChanges();
    expect(fixture.componentInstance.areas()).toEqual([area('runner')]);
    expect(fixture.nativeElement.textContent).toContain('Beta runner definition');
    expect(fixture.nativeElement.textContent).toContain('Beta dossier');
    expect(fixture.nativeElement.textContent).not.toContain('Alpha dossier');
  });

  it('removes displayed content immediately on a project change', () => {
    const fixture = mount();
    http.expectOne('/api/projects/Alpha/areas').flush({ items: [area('runner')] });
    fixture.componentInstance.select('runner');
    fixture.detectChanges();
    http.expectOne('/api/projects/Alpha/areas/runner/glossary').flush(glossary('runner', 'Alpha runner'));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Alpha runner definition');
    fixture.componentRef.setInput('projectName', 'Beta');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('Alpha runner definition');
    expect(fixture.componentInstance.areas()).toEqual([]);
    http.expectOne('/api/projects/Beta/areas').flush({ items: [] });
  });

  it('cancels glossary loading when cleared and all outstanding loads on destruction', () => {
    const fixture = mount();
    const areas = http.expectOne('/api/projects/Alpha/areas');
    fixture.componentInstance.select('runner');
    fixture.detectChanges();
    const request = http.expectOne('/api/projects/Alpha/areas/runner/glossary');
    fixture.componentInstance.select('');
    fixture.detectChanges();
    expect(request.cancelled).toBe(true);
    expect(fixture.componentInstance.glossary()).toBeNull();
    fixture.destroy();
    expect(areas.cancelled).toBe(true);
    expect(catalogues.get('Alpha')!.observed).toBe(false);
  });
});

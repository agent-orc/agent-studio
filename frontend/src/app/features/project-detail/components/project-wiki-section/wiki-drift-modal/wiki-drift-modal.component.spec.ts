import { afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiDriftModalComponent } from './wiki-drift-modal.component';
import type { WikiLinkedElement } from '../wiki-linked-element';

/**
 * AGT-2819 split the page-focused drift dialog out of
 * `project-wiki-section.ts`. The Knowledge section's own test still proves the
 * dialog opens and shows a document-scoped prompt; these cases cover the
 * dialog's own contract.
 */
async function build(inputs: Record<string, unknown> = {}) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [WikiDriftModalComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(WikiDriftModalComponent);
  fixture.componentRef.setInput('projectName', 'demo');
  for (const [name, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(name, value);
  }
  fixture.detectChanges();
  return { fixture, http: TestBed.inject(HttpTestingController) };
}

/** Answers the three requests the dialog fires when it opens. */
function flushOpen(http: HttpTestingController, prompt = 'Base architecture drift prompt') {
  http.expectOne('/api/cli/claude/models').flush({ models: [], source: 'test' });
  http.expectOne('/api/watch-paths').flush([{ name: 'demo', path: '/repo/demo' }]);
  http.expectOne(req => req.method === 'GET' && req.urlWithParams.startsWith('/api/drift/demo/reports?'))
    .flush({ reports: [] });
  http.expectOne('/api/drift/demo/actions/software-architecture-drift/prompt').flush({ prompt });
}

afterEach(() => {
  TestBed.resetTestingModule();
});

describe('WikiDriftModalComponent', () => {
  it('loads its material when it opens', async () => {
    const { fixture, http } = await build({ docRel: 'docs/model.md', docTitle: 'Model' });
    flushOpen(http);
    fixture.detectChanges();

    expect(fixture.componentInstance.driftPrompt()).toBe('Base architecture drift prompt');
    expect(fixture.componentInstance.driftPromptLoading()).toBe(false);
    http.verify();
    fixture.destroy();
  });

  it('the prompt is scoped to the open page and carries its linked elements', async () => {
    const links: WikiLinkedElement[] = [
      { kind: 'doc', label: 'Architecture model', target: 'docs/system/model.md', taskReference: null },
    ];
    const { fixture, http } = await build({
      docRel: 'docs/model.md',
      docTitle: 'Model',
      docFolder: 'docs',
      docKindLabel: 'Concept',
      docLinks: links,
    });
    flushOpen(http);
    fixture.detectChanges();

    const text = document.body.querySelector('[data-testid="project-wiki-drift-result"]')?.textContent ?? '';
    expect(text).toContain('Knowledge page drift analysis: Model');
    expect(text).toContain('Page: docs/model.md');
    expect(text).toContain('Category: docs');
    expect(text).toContain('Page type: Concept');
    expect(text).toContain('docs/system/model.md');
    expect(text).toContain('Base architecture drift prompt');
    expect(text).toContain('docs/.drift/docs-model-md.md');
    http.verify();
    fixture.destroy();
  });

  it('the root folder is named honestly when no page is open', async () => {
    const { fixture, http } = await build({ docRel: null, docTitle: '' });
    flushOpen(http);
    fixture.detectChanges();

    const text = document.body.querySelector('[data-testid="project-wiki-drift-result"]')?.textContent ?? '';
    expect(text).toContain('Knowledge page drift analysis: Root folder');
    expect(text).toContain('Page: (root folder)');
    expect(text).toContain('Category: root folder');
    http.verify();
    fixture.destroy();
  });

  it('a backdrop click dismisses, a click inside the card does not', async () => {
    const { fixture, http } = await build({ docRel: 'docs/model.md' });
    flushOpen(http);
    fixture.detectChanges();
    let closed = 0;
    fixture.componentInstance.closeRequest.subscribe(() => closed++);

    const card = document.body.querySelector('.pwiki__drift-card') as HTMLElement;
    fixture.componentInstance.requestClose({ target: card } as unknown as Event);
    expect(closed).toBe(0);

    const backdrop = document.body.querySelector('[data-testid="project-wiki-drift-modal"]') as HTMLElement;
    fixture.componentInstance.requestClose({ target: backdrop } as unknown as Event);
    expect(closed).toBe(1);
    http.verify();
    fixture.destroy();
  });

  it('changing the CLI clears the model and loads that CLI catalogue', async () => {
    const { fixture, http } = await build({ docRel: 'docs/model.md' });
    flushOpen(http);
    fixture.componentInstance.onDriftModelChange('claude-opus-4-8');

    fixture.componentInstance.onDriftCliChange('codex');

    expect(fixture.componentInstance.driftCli()).toBe('codex');
    expect(fixture.componentInstance.driftModel()).toBe('');
    http.expectOne('/api/cli/codex/models').flush({ models: [], source: 'test' });
    http.verify();
    fixture.destroy();
  });

  it('an unknown CLI selection falls back to claude on the already-loaded catalogue', async () => {
    const { fixture, http } = await build({ docRel: 'docs/model.md' });
    flushOpen(http);

    fixture.componentInstance.onDriftCliChange('nonsense');

    expect(fixture.componentInstance.driftCli()).toBe('claude');
    // The dialog loaded claude's catalogue when it opened, and CliCatalogStore
    // serves it from cache, so the fallback costs no second round trip.
    http.verify();
    fixture.destroy();
  });

  it('a failed prompt load is reported instead of leaving the dialog spinning', async () => {
    const { fixture, http } = await build({ docRel: 'docs/model.md' });
    http.expectOne('/api/cli/claude/models').flush({ models: [], source: 'test' });
    http.expectOne('/api/watch-paths').flush([{ name: 'demo', path: '/repo/demo' }]);
    http.expectOne(req => req.method === 'GET' && req.urlWithParams.startsWith('/api/drift/demo/reports?'))
      .flush({ reports: [] });
    http.expectOne('/api/drift/demo/actions/software-architecture-drift/prompt')
      .flush({ error: 'No architecture model' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(fixture.componentInstance.driftPromptLoading()).toBe(false);
    expect(fixture.componentInstance.driftError()).toBe('No architecture model');
    http.verify();
    fixture.destroy();
  });

  it('the selected-model label is honest when the CLI default is in use', async () => {
    const { fixture, http } = await build({ docRel: 'docs/model.md' });
    flushOpen(http);

    expect(fixture.componentInstance.selectedModelLabel()).toBe('CLI default');
    http.verify();
    fixture.destroy();
  });
});

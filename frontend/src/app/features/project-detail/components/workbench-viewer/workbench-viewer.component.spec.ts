import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
import type { WorkbenchDocument } from '../../../../models/project-docs.model';
import {
  ISOLATED_HTML_LINK_MESSAGE,
  WORKBENCH_DECISION_ANCHOR_MESSAGE,
  WORKBENCH_DECISION_CHANGE_MESSAGE,
  WORKBENCH_DECISION_FOCUS_ACTION_MESSAGE,
} from '../../../../services/sandboxed-html.util';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { TaskService } from '../../../../services/task.service';
import { WorkbenchViewerComponent } from './workbench-viewer.component';

const DOCUMENT: WorkbenchDocument = {
  workbench: {
    id: 'boundary',
    key: 'DEM-W4',
    title: 'Boundary probe',
    summary: 'Proves the isolated wrapper.',
    status: 'active',
    phase: 'testing',
    updatedAtUtc: '2026-07-12T10:00:00Z',
    entryPath: 'docs/workbenches/boundary/index.html',
    valid: true,
    error: null,
    sourceTaskKeys: ['AGT-2123'],
    relatedTaskKeys: [],
    pattern: 'ui',
  },
  html: '<script id="early">document.body.dataset.ran="true"</script><html><head><base href="https://example.invalid/"><meta http-equiv="Content-Security-Policy" content="default-src *"></head><body class="artifact"><meta http-equiv="refresh" content="0;url=https://example.invalid/"><h1>Probe</h1><section data-decision-id="route" data-decision-kind="single"><strong>Choose route</strong><span data-option-id="direct">Direct</span><span data-option-id="queue">Queue</span><span data-comment="Optional note"></span></section></body></html>',
  branch: 'develop',
  revision: null,
  workingTreeModified: true,
  fingerprint: null,
};

describe('WorkbenchViewerComponent', () => {
  beforeEach(() => sessionStorage.clear());

  it('ends loading and shows the API reason when a listed Dossier cannot be read', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskService, useValue: { getReferenceStatuses: () => of([]), refresh: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('workbenchId', 'missing');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http
      .expectOne('/api/projects/Demo/workbenches/missing')
      .flush(
        { error: 'Dossier entrypoint is missing.' },
        { status: 404, statusText: 'Not Found' },
      );
    fixture.detectChanges();

    expect(fixture.componentInstance.loading()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-viewer-loading"]'))
      .toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-viewer-error"]')?.textContent)
      .toContain('Dossier entrypoint is missing.');

    const hub = TestBed.inject(JobsHubClient);
    hub.connected.set(true);
    fixture.componentInstance.refreshManually();
    http.expectNone('/api/projects/Demo/workbenches/missing');

    hub.connected.set(false);
    fixture.componentInstance.refreshManually();
    http.expectOne('/api/projects/Demo/workbenches/missing').flush(
      { error: 'Still unavailable.' },
      { status: 503, statusText: 'Unavailable' },
    );
    http.verify();
  });

  it('emits the resolved document once, carrying the catalogue key', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskService, useValue: { getReferenceStatuses: () => of([]), refresh: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('workbenchId', 'boundary');
    const resolved = vi.fn();
    fixture.componentInstance.documentResolved.subscribe(resolved);
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/workbenches/boundary').flush(DOCUMENT);
    fixture.detectChanges();

    expect(resolved).toHaveBeenCalledTimes(1);
    expect(resolved).toHaveBeenCalledWith(DOCUMENT);
    expect(resolved.mock.calls[0][0].workbench.key).toBe('DEM-W4');

    const pendingReferences = http.match('/api/projects/Demo/workbenches/DEM-W4/references');
    pendingReferences.forEach(request => request.flush({
      projectName: 'Demo',
      workbenchKey: 'DEM-W4',
      workbenchId: 'boundary',
      legacyTaskKeys: [],
      items: [],
    }));
  });

  it('normalises artifact HTML behind a policy-first fixed wrapper', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    });
    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: TaskService,
          useValue: { getReferenceStatuses: () => of([]), refresh: vi.fn() },
        },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('workbenchId', 'boundary');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Demo/workbenches/boundary').flush({
      ...DOCUMENT,
      workbench: { ...DOCUMENT.workbench, phase: 'decision-ready' },
      revision: 'a'.repeat(40),
      workingTreeModified: false,
      fingerprint: 'b'.repeat(64),
    });
    fixture.detectChanges();
    expect(fixture.componentInstance.lastUpdatedAtUtc()).not.toBeNull();
    await fixture.whenStable();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/workbenches/DEM-W4/references').flush({
      projectName: 'Demo',
      workbenchKey: 'DEM-W4',
      workbenchId: 'boundary',
      legacyTaskKeys: [],
      items: [],
    });
    fixture.detectChanges();

    const srcdoc = fixture.componentInstance.srcdoc();
    const parsed = new DOMParser().parseFromString(srcdoc, 'text/html');
    expect(parsed.head.firstElementChild?.tagName).toBe('META');
    expect(parsed.head.firstElementChild?.getAttribute('http-equiv')).toBe(
      'Content-Security-Policy',
    );
    expect(parsed.head.children.item(1)?.tagName).toBe('BASE');
    expect(srcdoc.indexOf('Content-Security-Policy')).toBeLessThan(srcdoc.indexOf('id="early"'));
    expect(parsed.querySelectorAll('meta[http-equiv="Content-Security-Policy"]')).toHaveLength(1);
    expect(parsed.querySelector('meta[http-equiv="refresh"]')).toBeNull();
    expect(parsed.querySelector('base')?.getAttribute('href')).toBe('about:blank');
    expect(parsed.body.classList.contains('artifact')).toBe(true);
    expect(parsed.documentElement.dataset['documentPattern']).toBe('ui');

    const frame = fixture.nativeElement.querySelector(
      '[data-testid="workbench-viewer-frame"]',
    ) as HTMLIFrameElement;
    expect(frame.getAttribute('sandbox')).toBe('allow-scripts');
    expect(frame.getAttribute('title')).toBe('Dossier artifact: Boundary probe');
    expect(frame.srcdoc).toBe(srcdoc);
    expect(srcdoc).toContain(ISOLATED_HTML_LINK_MESSAGE);
    expect(srcdoc).toContain(WORKBENCH_DECISION_CHANGE_MESSAGE);
    expect(srcdoc).toContain(WORKBENCH_DECISION_ANCHOR_MESSAGE);
    expect(srcdoc).toContain(WORKBENCH_DECISION_FOCUS_ACTION_MESSAGE);
    expect(fixture.componentInstance.decisionMarkup().points[0].id).toBe('route');
    expect(document.querySelector('[data-testid="workbench-viewer-working-tree"]')).toBeNull();
    expect(document.querySelector('[data-testid="workbench-decision-panel"]')).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-testid="workbench-viewer-open-decisions"]')
        ?.textContent,
    ).toContain('1 open');
    fixture.componentInstance.onFrameMessage({
      source: frame.contentWindow,
      data: {
        type: WORKBENCH_DECISION_CHANGE_MESSAGE,
        responses: [
          {
            decisionId: 'route',
            kind: 'single',
            selectedOptionIds: ['direct'],
            comment: 'Ship it.',
          },
        ],
      },
    } as MessageEvent);
    fixture.detectChanges();
    expect(fixture.componentInstance.decisionResponses()).toEqual([
      {
        decisionId: 'route',
        kind: 'single',
        selectedOptionIds: ['direct'],
        comment: 'Ship it.',
      },
    ]);

    fixture.componentInstance.onFrameMessage({
      source: frame.contentWindow,
      data: {
        type: WORKBENCH_DECISION_ANCHOR_MESSAGE,
        rect: { top: 320, left: 48, width: 640, height: 72 },
        visible: true,
      },
    } as MessageEvent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const inlineAction = fixture.nativeElement.querySelector(
      '[data-testid="workbench-inline-decision-action"]') as HTMLElement;
    expect(inlineAction).not.toBeNull();
    expect(inlineAction.style.top).toBe('320px');
    expect(inlineAction.querySelector('[data-testid="workbench-decision-start"]')).not.toBeNull();

    const inlineStart = inlineAction.querySelector(
      '[data-testid="workbench-decision-start"]') as HTMLButtonElement;
    const focus = vi.spyOn(inlineStart, 'focus');
    fixture.componentInstance.onFrameMessage({
      source: frame.contentWindow,
      data: { type: WORKBENCH_DECISION_FOCUS_ACTION_MESSAGE },
    } as MessageEvent);
    expect(focus).toHaveBeenCalledOnce();
    expect(
      fixture.nativeElement.querySelector('[data-testid="workbench-viewer-open-decisions"]')
        ?.textContent,
    ).toContain('0 open');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-decision-draft-notice"]'))
      .not.toBeNull();

    const referenceChip = fixture.nativeElement.querySelector(
      '[data-testid="workbench-viewer-key"]',
    ) as HTMLButtonElement;
    expect(referenceChip.textContent).toContain('DEM-W4');
    expect(referenceChip.getAttribute('aria-label')).toBe('Copy key DEM-W4');
    referenceChip.click();
    expect(writeText).toHaveBeenCalledWith('DEM-W4');

    const wikiTargets: { relPath: string; reuse: 'replace-current' | 'new' }[] = [];
    fixture.componentInstance.openWiki.subscribe((path) => wikiTargets.push(path));
    expect(document.querySelector('[data-testid="workbench-viewer-open-wiki"]')).toBeNull();

    fixture.componentInstance.onFrameMessage({
      source: frame.contentWindow,
      data: { type: ISOLATED_HTML_LINK_MESSAGE, href: '../target/index.html' },
    } as MessageEvent);
    expect(wikiTargets).toEqual([{
      relPath: 'workbenches/target/index.html', reuse: 'replace-current',
    }]);

    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null);
    fixture.componentInstance.onFrameMessage({
      source: frame.contentWindow,
      data: { type: ISOLATED_HTML_LINK_MESSAGE, href: 'https://example.com/reference' },
    } as MessageEvent);
    expect(openSpy).toHaveBeenCalledWith(
      'https://example.com/reference',
      '_blank',
      'noopener,noreferrer',
    );
    openSpy.mockRestore();

    const maximize = fixture.nativeElement.querySelector(
      '[data-testid="workbench-viewer-maximize"]',
    ) as HTMLButtonElement;
    maximize.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.maximized()).toBe(true);
    expect(maximize.getAttribute('aria-label')).toBe('Restore');
    fixture.componentInstance.exitMaximized();
    expect(fixture.componentInstance.maximized()).toBe(false);

    TestBed.inject(JobsHubClient).workbenchEvent.set({
      type: 'updated',
      projectName: 'Demo',
      workbenchId: 'boundary',
      workbench: null,
      previousStatus: 'active',
      occurredAtUtc: new Date().toISOString(),
    });
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/workbenches/boundary').flush({
      ...DOCUMENT,
      workbench: { ...DOCUMENT.workbench, summary: 'Updated through the live viewer path.' },
    });
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/workbenches/DEM-W4/references').flush({
      projectName: 'Demo',
      workbenchKey: 'DEM-W4',
      workbenchId: 'boundary',
      legacyTaskKeys: [],
      items: [],
    });
    expect(fixture.componentInstance.document()?.workbench.summary).toBe(
      'Updated through the live viewer path.',
    );
    http.verify();
  });

  it('offers the documented transition after every referenced card is terminal', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskService, useValue: { getReferenceStatuses: () => of([]), refresh: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('workbenchId', 'boundary');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const ready: WorkbenchDocument = {
      ...DOCUMENT,
      workbench: {
        ...DOCUMENT.workbench,
        status: 'decided',
        documentation: {
          eligible: true,
          totalCount: 2,
          terminalCount: 2,
          openCount: 0,
          missingCount: 0,
          references: [
            { key: 'AGT-1', exists: true, terminal: true, lane: '6-completed' },
            { key: 'AGT-2', exists: true, terminal: true, lane: '7-archive' },
          ],
        },
      },
      revision: 'a'.repeat(40),
      workingTreeModified: false,
      fingerprint: 'b'.repeat(64),
    };
    http.expectOne('/api/projects/Demo/workbenches/boundary').flush(ready);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/workbenches/DEM-W4/references').flush({
      projectName: 'Demo',
      workbenchKey: 'DEM-W4',
      workbenchId: 'boundary',
      legacyTaskKeys: [],
      items: [],
    });

    const notice = fixture.nativeElement.querySelector(
      '[data-testid="workbench-documentation-ready"]') as HTMLElement;
    expect(notice.textContent).toContain('All referenced cards are terminal');
    (notice.querySelector('[data-testid="workbench-documentation-confirm"]') as HTMLButtonElement).click();

    const transition = http.expectOne('/api/projects/Demo/workbenches/boundary/document');
    expect(transition.request.method).toBe('POST');
    expect(transition.request.body).toEqual({
      actor: 'Operator',
      expectedRevision: 'a'.repeat(40),
      expectedFingerprint: 'b'.repeat(64),
    });
    transition.flush({
      success: true,
      errorCode: null,
      error: null,
      workbenchId: 'boundary',
      status: 'documented',
      revision: 'c'.repeat(40),
      fingerprint: 'd'.repeat(64),
      idempotent: false,
    });
    http.expectOne('/api/projects/Demo/workbenches/boundary').flush({
      ...ready,
      workbench: { ...ready.workbench, status: 'documented', documentation: null },
    });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/workbenches/DEM-W4/references').flush({
      projectName: 'Demo',
      workbenchKey: 'DEM-W4',
      workbenchId: 'boundary',
      legacyTaskKeys: [],
      items: [],
    });

    expect(fixture.nativeElement.querySelector('[data-testid="workbench-documentation-ready"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('.decision-panel__stage')?.textContent).toContain('Documented');
    http.verify();
  });
});

import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { FileSourceHistoryComponent } from './file-source-history.component';

describe('FileSourceHistoryComponent', () => {
  async function setup() {
    await TestBed.configureTestingModule({
      imports: [FileSourceHistoryComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(FileSourceHistoryComponent);
    fixture.componentRef.setInput('jobId', 'demo-job');
    fixture.componentRef.setInput('watchPath', 'C:/projects/foo');
    fixture.componentRef.setInput('path', 'aspect-code-quality.md');
    fixture.componentRef.setInput('content', '# Current\n\nLatest body.');
    fixture.detectChanges();
    return fixture;
  }

  it('keeps the current file quiet and loads a selected version only inside history', async () => {
    const fixture = await setup();
    const http = TestBed.inject(HttpTestingController);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="file-source-history"]')?.textContent).toContain('Current');
    expect(root.querySelector('[data-testid="file-source-version-select"]')).toBeNull();
    expect(root.querySelector('[data-testid="file-source-diff-panel"]')).toBeNull();

    root.querySelector<HTMLButtonElement>('[data-testid="file-source-history-toggle"]')?.click();
    fixture.detectChanges();

    http.expectOne((r) =>
      r.url === '/api/tasks/demo-job/files/aspect-code-quality.md/history' &&
      r.params.get('watchPath') === 'C:/projects/foo')
      .flush([
        {
          sha: '2222222',
          at: '2026-06-09T12:10:00Z',
          runIndex: 2,
          verdict: 'pass',
          message: 'update aspect result',
          author: 'Agent <agent@example.com>',
          provenance: { source: 'workspace', path: 'aspect-code-quality.md' },
        },
        {
          sha: '1111111',
          at: '2026-06-09T12:00:00Z',
          runIndex: 1,
          verdict: 'concerns',
          message: 'create aspect result',
          author: 'Agent <agent@example.com>',
          provenance: { source: 'workspace', path: 'aspect-code-quality.md' },
        },
      ]);

    http.expectOne((r) =>
      r.url === '/api/tasks/demo-job/files/aspect-code-quality.md' &&
      r.params.get('at') === '2222222')
      .flush(utf8Buffer('# Version 2\n\nPass.'));
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="file-source-history-timeline"]')?.textContent).toContain('Run #2');
    expect(root.querySelector('[data-testid="file-source-history-timeline"]')?.textContent).toContain('pass');
    const verdicts = root.querySelectorAll('[data-testid="file-source-history-verdict"]');
    expect(verdicts[0].getAttribute('data-tone')).toBe('success');
    expect(verdicts[1].getAttribute('data-tone')).toBe('warning');
    expect(root.querySelector('[data-testid="file-source-version"]')?.textContent).toContain('Version 2');
    expect(root.querySelector('[data-testid="file-source-version-select"]')).toBeNull();
    expect(root.querySelector('[data-testid="file-source-diff-panel"]')).toBeNull();
    http.expectNone((r) => r.url.endsWith('/diff'));

    root.querySelector<HTMLButtonElement>('[data-testid="file-source-history-run-1"]')?.click();
    fixture.detectChanges();
    http.expectOne((r) =>
      r.url === '/api/tasks/demo-job/files/aspect-code-quality.md' &&
      r.params.get('at') === '1111111')
      .flush(utf8Buffer('# Version 1\n\nConcerns.'));
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="file-source-version"]')?.textContent).toContain('Version 1');
    http.verify();
  });

  it('opens directly on the git timeline and loads it eagerly when initialMode is "history"', async () => {
    await TestBed.configureTestingModule({
      imports: [FileSourceHistoryComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(FileSourceHistoryComponent);
    fixture.componentRef.setInput('jobId', 'demo-job');
    fixture.componentRef.setInput('watchPath', 'C:/projects/foo');
    fixture.componentRef.setInput('path', 'status.md');
    fixture.componentRef.setInput('content', '# Status\n\nPassed.');
    fixture.componentRef.setInput('initialMode', 'history');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    const root = fixture.nativeElement as HTMLElement;

    // No interaction needed: the timeline request fires on first render.
    http.expectOne((r) => r.url === '/api/tasks/demo-job/files/status.md/history')
      .flush([
        {
          sha: 'aaaaaaa',
          at: '2026-06-09T12:10:00Z',
          runIndex: 1,
          verdict: 'pass',
          message: 'write status',
          author: 'Agent <agent@example.com>',
          provenance: { source: 'workspace', path: 'status.md' },
        },
      ]);
    http.expectOne((r) =>
      r.url === '/api/tasks/demo-job/files/status.md' && r.params.get('at') === 'aaaaaaa')
      .flush(utf8Buffer('# Status\n\nPassed.'));
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="file-source-history-panel"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="file-source-history-timeline"]')?.textContent).toContain('Run #1');
    http.verify();
  });
});

function utf8Buffer(value: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(value);
  const buffer = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}

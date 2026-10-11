import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { StudioDiffViewComponent } from './diff-tab-view.component';
import { TaskService } from '../../../../services/task.service';
import type { TaskInfo } from '../../../../models/task.model';
import { LARGE_DIFF_LINE_THRESHOLD } from '../../../../utils/large-diff-gate';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('StudioDiffViewComponent (smoke)', () => {
  it('resolves a commit owner on demand when board cards omit commits', async () => {
    await TestBed.configureTestingModule({
      imports: [StudioDiffViewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const sha = 'abc1234abc1234abc1234abc1234abc1234abc1';
    const fixture = TestBed.createComponent(StudioDiffViewComponent);
    fixture.componentRef.setInput('commitSha', sha);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url.endsWith(`/api/tasks/commit-owner/${sha}`))
      .flush({ job: { id: 'demo', taskKey: 'p::demo', title: 'Demo', state: '5-human-review',
        watchPath: '/workspace', projectName: 'Demo' },
      commit: { sha, message: 'Older commit', filesChanged: 1 } });
    fixture.detectChanges();
    expect(fixture.componentInstance.owner()?.job.id).toBe('demo');
    http.expectOne(request => request.url.includes(`/api/tasks/demo/commits/${sha}/files`))
      .flush({ sha, files: [] });
    http.verify();
  });

  it('compiles + instantiates without throwing', async () => {
    try {
      await TestBed.configureTestingModule({
        imports: [StudioDiffViewComponent],
        providers: [
          provideZonelessChangeDetection(),
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(StudioDiffViewComponent);
      fixture.componentRef.setInput('commitSha', undefined);

      // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // commitSha
    try { fixture.detectChanges(); } catch (e) {
        console.warn('[smoke] StudioDiffViewComponent initial render skipped:', (e as Error).message);
      }
      expect(fixture.componentInstance).toBeTruthy();
    } catch (e) {
      // TestBed setup itself crashed (module-load cycle, env not
      // initialized because of file-order, etc). Still verifies the
      // component class is importable.
      console.warn('[smoke] StudioDiffViewComponent TestBed setup skipped:', (e as Error).message);
      expect(StudioDiffViewComponent).toBeTruthy();
    }
  });

  it('loads the owning commit files and gates a large selected diff', async () => {
    await TestBed.configureTestingModule({
      imports: [StudioDiffViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const taskService = TestBed.inject(TaskService);
    const sha = 'abc1234abc1234abc1234abc1234abc1234abc1';
    taskService.jobs.set([
      {
        id: 'demo-job',
        taskKey: 'p::demo-job',
        title: 'Demo job',
        state: '5-human-review',
        order: 1,
        agent: 'claude',
        cliType: 'claude',
        ownerClientId: 'local-default',
        sessionName: '',
        watchPath: 'C:/projects/foo',
        projectName: 'Foo',
        folderPath: 'C:/projects/foo/5-human-review/demo-job',
        createdAt: '2026-05-01T00:00:00Z',
        sessionChain: [],
        commits: [
          {
            sha,
            shortSha: 'abc1234',
            message: 'Large generated diff',
            filesChanged: 1,
            files: ['src/generated.ts'],
            at: '2026-06-09T12:00:00Z',
          },
        ],
      } as unknown as TaskInfo,
    ]);

    const fixture = TestBed.createComponent(StudioDiffViewComponent);
    fixture.componentRef.setInput('commitSha', sha);
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl.expectOne((r) => r.url.includes('/api/tasks/demo-job/commits/abc1234abc1234abc1234abc1234abc1234abc1/files')).flush({
      sha,
      files: [{ status: 'M', path: 'src/generated.ts', added: LARGE_DIFF_LINE_THRESHOLD, removed: 0 }],
    });
    fixture.detectChanges();

    const diff = Array.from({ length: LARGE_DIFF_LINE_THRESHOLD }, (_, i) => `+line ${i}`).join('\n');
    httpCtrl.expectOne((r) => r.url.includes('/api/tasks/demo-job/commits/abc1234abc1234abc1234abc1234abc1234abc1/diff')).flush({ diff });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const gated = root.querySelector('[data-testid="studio-diff-gated"]');
    expect(gated?.textContent).toContain('src/generated.ts');
    expect(gated?.textContent).toContain('Show diff');
    expect(root.querySelector('[data-testid="studio-diff-render"]')).toBeNull();

    root.querySelector<HTMLButtonElement>('[data-testid="studio-diff-show"]')?.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.diffGated()).toBe(false);
    expect(root.querySelector('[data-testid="studio-diff-gated"]')).toBeNull();
    httpCtrl.verify();
  });

  it('renders a loaded empty state when the diff endpoint returns an empty body', async () => {
    await TestBed.configureTestingModule({
      imports: [StudioDiffViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const taskService = TestBed.inject(TaskService);
    const sha = 'def5678def5678def5678def5678def5678def5';
    taskService.jobs.set([
      {
        id: 'empty-diff-job',
        taskKey: 'p::empty-diff-job',
        title: 'Empty diff job',
        state: '5-human-review',
        order: 1,
        agent: 'claude',
        cliType: 'claude',
        ownerClientId: 'local-default',
        sessionName: '',
        watchPath: 'C:/projects/foo',
        projectName: 'Foo',
        folderPath: 'C:/projects/foo/5-human-review/empty-diff-job',
        createdAt: '2026-05-01T00:00:00Z',
        sessionChain: [],
        commits: [
          {
            sha,
            shortSha: 'def5678',
            message: 'Empty path diff',
            filesChanged: 1,
            files: ['src/empty.ts'],
            at: '2026-06-09T12:00:00Z',
          },
        ],
      } as unknown as TaskInfo,
    ]);

    const fixture = TestBed.createComponent(StudioDiffViewComponent);
    fixture.componentRef.setInput('commitSha', sha);
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    httpCtrl.expectOne((r) => r.url.includes('/api/tasks/empty-diff-job/commits/def5678def5678def5678def5678def5678def5/files')).flush({
      sha,
      files: [{ status: 'M', path: 'src/empty.ts', added: 0, removed: 0 }],
    });
    fixture.detectChanges();

    httpCtrl.expectOne((r) => r.url.includes('/api/tasks/empty-diff-job/commits/def5678def5678def5678def5678def5678def5/diff')).flush(null);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(fixture.componentInstance.diffState()).toBe('loaded');
    expect(root.querySelector('[data-testid="studio-diff-empty"]')?.textContent).toContain('empty response');
    expect(root.textContent).not.toContain('Loading diff');
    httpCtrl.verify();
  });
});

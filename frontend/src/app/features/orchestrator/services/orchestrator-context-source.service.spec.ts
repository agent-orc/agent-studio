import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { OrchestratorContextSourceService } from './orchestrator-context-source.service';

describe('OrchestratorContextSourceService', () => {
  it('keeps search results project-bound and maps every source group to stable refs', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const service = TestBed.inject(OrchestratorContextSourceService);
    const http = TestBed.inject(HttpTestingController);
    const resultPromise = firstValueFrom(service.search('Demo', 'context'));

    http.expectOne(request => request.url === '/api/search' && request.params.get('project') === 'Demo').flush({
      query: 'context',
      tasks: [{ taskId: 'tsk_1', taskKey: 'DEMO-4', title: 'Context task', projectId: 'Demo', state: '2-ready' }],
    });
    http.expectOne(request => request.url === '/api/search/repository' && request.params.get('domains') === 'commits,files').flush({
      files: [{ domain: 'files', projectName: 'Demo', title: 'context.ts', subtitle: 'src/context.ts', path: 'src/context.ts' }],
      commits: [{ domain: 'commits', projectName: 'Demo', title: 'feat: add context', subtitle: '01234567', sha: '0123456789abcdef' }],
    });
    http.expectOne(request => request.url === '/api/projects/Demo/wiki/search').flush({
      query: 'context', semanticUsed: false, expandedTerms: [], durationMs: 1,
      results: [{ relPath: 'concepts/context.md', title: 'Context model', kind: 'md', snippet: '', score: 1, updatedAt: null }],
    });
    http.expectOne('/api/projects/Demo/workbenches').flush({
      projectName: 'Demo', includesHistory: false, count: 1,
      items: [{ id: 'context-workbench', key: 'CTX', title: 'Context workbench', summary: 'Inspect context', status: 'active', phase: 'testing', updatedAtUtc: '2026-08-10T10:00:00Z', entryPath: 'operations/context/index.html', valid: true, error: null, sourceTaskKeys: [] }],
    });

    const result = await resultPromise;
    expect(result.tasks.map(item => item.reference.reference)).toEqual(['DEMO-4']);
    expect(result.files[0].reference).toMatchObject({ kind: 'repository-file', reference: 'src/context.ts' });
    expect(result.commits[0].reference).toMatchObject({ kind: 'commit', reference: 'commit:Demo/0123456789abcdef' });
    expect(result.wiki.map(item => item.reference.kind)).toEqual(['page', 'page']);
  });
});

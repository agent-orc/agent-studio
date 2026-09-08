import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { OrchestratorContextDigestService } from './orchestrator-context-digest.service';

describe('OrchestratorContextDigestService.scopeLabel', () => {
  function service(): OrchestratorContextDigestService {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        OrchestratorContextDigestService,
      ],
    });
    return TestBed.inject(OrchestratorContextDigestService);
  }

  it('labels every context kind, including the Dossier (workbench) scope', () => {
    const svc = service();
    expect(svc.scopeLabel()).toBe('No context');

    svc.selectContext('global');
    expect(svc.scopeLabel()).toBe('Global context');

    svc.selectContext('project:Agent Studio');
    expect(svc.scopeLabel()).toBe('Project context');

    svc.selectContext('workbench:Agent Studio/AGT-W43');
    expect(svc.scopeLabel()).toBe('Dossier context');

    svc.selectContext('task:Agent Studio/AGT-1916');
    expect(svc.scopeLabel()).toBe('Task context');
  });
});

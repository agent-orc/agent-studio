import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { ProjectDeployPipelineComponent } from './project-deploy-pipeline';

describe('ProjectDeployPipelineComponent', () => {
  it('renders the project definition returned by the API', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeployPipelineComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeployPipelineComponent);
    fixture.componentRef.setInput('projectName', 'Website');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Website/pipeline-definition').flush({
      project: 'Website', path: '.agent-studio/project.yml', exists: true, schemaVersion: 2,
      valid: true, issues: [], migrationAvailable: false,
      diagram: {
        schemaVersion: 2,
        gate: { source: 'pipeline', build: ['npm run website-build'], test: ['npm run website-test'], testSuites: [] },
        artifacts: [{ path: 'artifacts[0]', name: 'site', type: 'static', source: 'dist' }],
        environmentsDefaulted: false, lifecycleDefaulted: false,
        environments: [{ path: 'environments[0]', name: 'production', order: 0,
          protected: true, automatic: false, approval: ['Maintainer'], requires: null,
          steps: [{ path: 'deploy[0]', kind: 'publish-static', artifact: 'site', target: 'web-vm' }],
          variables: [] }],
      },
    });
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="pipeline-node-gate"]')?.textContent).toContain('npm run website-build');
    expect(host.querySelector('[data-testid="pipeline-node-production"]')?.textContent).toContain('Publish to deploy branch');
    expect(host.textContent).not.toContain('commands.build');
    TestBed.inject(HttpTestingController).verify();
  });

  it('shows a load failure instead of a synthetic pipeline', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeployPipelineComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeployPipelineComponent);
    fixture.componentRef.setInput('projectName', 'Website');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Website/pipeline-definition')
      .flush({ error: 'Unavailable' }, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent)
      .toContain('could not be loaded');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="pipeline-node-gate"]')).toBeNull();
  });
});

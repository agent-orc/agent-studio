import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { ProjectEnvironmentDeclaration } from '../../../../models/project-overview.model';
import { ProjectEnvironmentOverviewComponent } from './project-environment-overview.component';

async function mount() {
  await TestBed.configureTestingModule({
    imports: [ProjectEnvironmentOverviewComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProjectEnvironmentOverviewComponent);
  fixture.componentRef.setInput('projectName', 'Alpha');
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  const request = http.expectOne('/api/projects/Alpha/environments');
  const statusRequest = http.expectOne('/api/projects/Alpha/deploy/status');
  return { fixture, request, statusRequest };
}

function declaration(id: string, name: string, kind: ProjectEnvironmentDeclaration['kind'] = 'stage'): ProjectEnvironmentDeclaration {
  return {
    id, name, kind, targetHostId: 'host', address: 'stage.example.test', template: 'container',
    destination: '/srv/app', containerPort: 8080, healthUrl: 'https://stage.example.test/health',
    approval: 'person', secretRefs: [],
  };
}

describe('ProjectEnvironmentOverviewComponent', () => {
  it('invites environment setup when no environment is declared', async () => {
    const { fixture, request, statusRequest } = await mount();
    request.flush({ project: 'Alpha', environments: [], promotions: [], failureCards: [] });
    statusRequest.flush([]);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-environments-empty"]')).toBeTruthy();
    expect(host.textContent).toContain('Set up environments in Deploy');
  });

  it('shows the recorded health of each environment', async () => {
    const { fixture, request, statusRequest } = await mount();
    request.flush({
      project: 'Alpha',
      environments: [
        { declaration: declaration('stage', 'Stage'), deployedArtifact: 'sha-abc', health: 'healthy', pendingArtifact: null, history: [] },
        { declaration: declaration('preview', 'Preview', 'other'), deployedArtifact: null, health: 'unknown', pendingArtifact: null, history: [] },
      ],
      promotions: [],
      failureCards: [],
    });
    statusRequest.flush([]);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-environment-stage"]')?.textContent).toContain('Healthy');
    expect(host.querySelector('[data-testid="project-environment-stage"]')?.textContent).toContain('Running sha-abc');
    expect(host.querySelector('[data-testid="project-environment-preview"]')?.textContent).toContain('Health unknown');
    expect(host.querySelector<HTMLElement>('[data-testid="project-environment-stage"] [data-testid="project-environment-health"]')?.dataset['tone']).toBe('success');
    expect(host.querySelector<HTMLElement>('[data-testid="project-environment-preview"] [data-testid="project-environment-health"]')?.dataset['tone']).toBe('neutral');
    expect(host.querySelector('[data-testid="project-environment-stage"] [data-testid="environment-runbooks"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="project-environment-preview"] [data-testid="environment-runbooks"]')).toBeNull();
  });

  it('shows uncertainty after a failed host report instead of naming its target', async () => {
    const { fixture, request, statusRequest } = await mount();
    const artifact = 'app@sha256:' + 'b'.repeat(64);
    request.flush({ project: 'Alpha', environments: [
      { declaration: declaration('stage', 'Stage'), deployedArtifact: artifact, health: 'failed', pendingArtifact: null,
        history: [{ at: '2026-11-16T12:00:00Z', status: 'failed', artifact, previousArtifact: null, evidence: 'health-http-failed' }] },
    ], promotions: [], failureCards: [] });
    statusRequest.flush([{
      environment: { id: 'stage' }, currentRelease: { version: '2026.11.16.1', artifactDigests: [artifact] },
      currentDeployment: null, newerRelease: null, cardsSinceLastDeploy: [], health: 'unhealthy', releaseUncertain: true,
    }]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="project-environment-stage"]').textContent)
      .toContain('Running release uncertain. A failed release may be running.');
  });
});

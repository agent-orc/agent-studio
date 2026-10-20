import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { ProjectEnvironmentDeclaration } from '../../../../models/project-overview.model';
import { ProjectEnvironmentOverviewComponent } from './project-environment-overview.component';

function mount() {
  TestBed.configureTestingModule({
    imports: [ProjectEnvironmentOverviewComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(ProjectEnvironmentOverviewComponent);
  fixture.componentRef.setInput('projectName', 'Alpha');
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  const request = http.expectOne('/api/projects/Alpha/environments');
  return { fixture, request };
}

function declaration(id: string, name: string, kind: ProjectEnvironmentDeclaration['kind'] = 'stage'): ProjectEnvironmentDeclaration {
  return {
    id, name, kind, targetHostId: 'host', address: 'stage.example.test', template: 'container',
    destination: '/srv/app', containerPort: 8080, healthUrl: 'https://stage.example.test/health',
    approval: 'person', secretRefs: [],
  };
}

describe('ProjectEnvironmentOverviewComponent', () => {
  it('invites environment setup when no environment is declared', () => {
    const { fixture, request } = mount();
    request.flush({ project: 'Alpha', environments: [], promotions: [], failureCards: [] });
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-environments-empty"]')).toBeTruthy();
    expect(host.textContent).toContain('Set up environments in Deploy');
  });

  it('shows the recorded health of each environment', () => {
    const { fixture, request } = mount();
    request.flush({
      project: 'Alpha',
      environments: [
        { declaration: declaration('stage', 'Stage'), deployedArtifact: 'sha-abc', health: 'healthy', pendingArtifact: null, history: [] },
        { declaration: declaration('preview', 'Preview', 'other'), deployedArtifact: null, health: 'unknown', pendingArtifact: null, history: [] },
      ],
      promotions: [],
      failureCards: [],
    });
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-environment-stage"]')?.textContent).toContain('Healthy');
    expect(host.querySelector('[data-testid="project-environment-stage"]')?.textContent).toContain('Running sha-abc');
    expect(host.querySelector('[data-testid="project-environment-preview"]')?.textContent).toContain('Health unknown');
    expect(host.querySelector('[data-testid="project-environment-stage"] [data-testid="environment-runbooks"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="project-environment-preview"] [data-testid="environment-runbooks"]')).toBeNull();
  });
});

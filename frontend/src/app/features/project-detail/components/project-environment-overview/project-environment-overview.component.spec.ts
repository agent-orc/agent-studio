import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ProjectEnvironmentOverviewComponent } from './project-environment-overview.component';

function mount(area: 'deploy' | 'operations') {
  TestBed.configureTestingModule({
    imports: [ProjectEnvironmentOverviewComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(ProjectEnvironmentOverviewComponent);
  fixture.componentRef.setInput('projectName', 'Alpha');
  fixture.componentRef.setInput('area', area);
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  const request = http.expectOne('/api/projects/Alpha/deployment/summary');
  return { fixture, request };
}

describe('ProjectEnvironmentOverviewComponent', () => {
  it('invites environment setup when inventory is empty', () => {
    const { fixture, request } = mount('operations');
    request.flush({ targets: [] });
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-environments-empty"]')).toBeTruthy();
    expect(host.textContent).toContain('Set up environments in Deploy');
  });

  it('reports application health as unknown until monitoring exists', () => {
    const { fixture, request } = mount('operations');
    request.flush({ targets: [{ id: 'staging', title: 'Staging', summary: 'Preview', runnable: true }] });
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-environment-staging"]')?.textContent)
      .toContain('Health unknown');
  });
});

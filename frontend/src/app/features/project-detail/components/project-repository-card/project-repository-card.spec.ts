import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ProjectRepositoryCardComponent } from './project-repository-card';

describe('ProjectRepositoryCardComponent', () => {
  async function render(settings: Record<string, unknown>) {
    await TestBed.configureTestingModule({
      imports: [ProjectRepositoryCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectRepositoryCardComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/settings').flush({ Demo: settings });
    fixture.detectChanges();
    return { fixture, http, root: fixture.nativeElement as HTMLElement };
  }

  it('marks both fields for remedy links and says when visibility is not recorded', async () => {
    const { root, http } = await render({ repositoryVisibility: null, integrationBranch: null });

    expect(root.querySelector('[data-setting-key="repository-visibility"]')).not.toBeNull();
    expect(root.querySelector('[data-setting-key="integration-branch"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="project-repository-visibility-unrecorded"]')).not.toBeNull();
    http.verify();
  });

  it('records the visibility and the integration branch through their project routes', async () => {
    const { fixture, http, root } = await render({ repositoryVisibility: null, integrationBranch: 'main' });

    (root.querySelector('[data-testid="project-repository-visibility-private"]') as HTMLButtonElement).click();
    const visibility = http.expectOne('/api/projects/Demo/repository-visibility');
    expect(visibility.request.method).toBe('PUT');
    expect(visibility.request.body).toEqual({ visibility: 'private' });
    visibility.flush({ repositoryVisibility: 'private' });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-repository-visibility-unrecorded"]')).toBeNull();

    const input = root.querySelector('[data-testid="project-integration-branch-input"]') as HTMLInputElement;
    input.value = ' develop ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (root.querySelector('[data-testid="project-integration-branch-save"]') as HTMLButtonElement).click();
    const branch = http.expectOne('/api/projects/Demo/integration-branch');
    expect(branch.request.body).toEqual({ branch: 'develop' });
    branch.flush({ integrationBranch: 'develop' });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-repository-notice"]')!.textContent)
      .toContain('Integration branch is set to develop.');
    http.verify();
  });

  it('restores the previous visibility when the save fails', async () => {
    const { fixture, http, root } = await render({ repositoryVisibility: 'public' });

    (root.querySelector('[data-testid="project-repository-visibility-private"]') as HTMLButtonElement).click();
    http.expectOne('/api/projects/Demo/repository-visibility').flush('no', { status: 500, statusText: 'Error' });
    fixture.detectChanges();

    expect(fixture.componentInstance.visibility()).toBe('public');
    expect(root.textContent).toContain('Repository visibility could not be saved.');
    http.verify();
  });
});

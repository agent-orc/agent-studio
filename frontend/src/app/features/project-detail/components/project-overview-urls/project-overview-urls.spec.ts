import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NEVER, Subject, of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { ProjectUrlProbeService, type ProjectUrlStatus } from '../../../../services/project-url-probe.service';
import { TaskService } from '../../../../services/task.service';
import { ProjectOverviewUrlsComponent } from './project-overview-urls';

const configuredUrl = {
  id: 'preview',
  label: 'Component preview',
  url: 'http://127.0.0.1:4311',
  sortOrder: 0,
  startRule: { command: 'npm run preview', cwd: null, port: 4311, source: 'manual' },
};

function workspace(name: string, id: string) {
  return [{
    id: 'ws',
    displayName: 'Workspace',
    projects: [{
      id,
      displayName: name,
      workspaceId: 'ws',
      storageLocation: `C:/tasks/${id}`,
      sortOrder: 0,
      archived: false,
      urls: [configuredUrl],
    }],
  }];
}

describe('ProjectOverviewUrlsComponent', () => {
  it('keeps an unknown URL quiet and offers Start only after it is known offline', async () => {
    const status = signal<ProjectUrlStatus>('unknown');
    const startProjectUrl = vi.fn(() => NEVER);
    await TestBed.configureTestingModule({
      imports: [ProjectOverviewUrlsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { getRegistryWorkspaces: () => of(workspace('Demo', 'PROJ-1')), startProjectUrl } },
        { provide: ProjectLookupService, useValue: { setWorkspaces: vi.fn() } },
        { provide: ProjectUrlProbeService, useValue: { statusFor: () => status(), refresh: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectOverviewUrlsComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();

    let host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-overview-url-status-preview"]')?.textContent).toContain('unknown');
    expect(host.querySelector('[data-testid="project-overview-url-start-preview"]')).toBeNull();

    status.set('offline');
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
    const start = host.querySelector<HTMLButtonElement>('[data-testid="project-overview-url-start-preview"]');
    expect(start?.getAttribute('aria-label')).toBe('Start Component preview');
    start!.click();
    fixture.detectChanges();

    expect(startProjectUrl).toHaveBeenCalledWith('PROJ-1', 'preview');
    expect(host.querySelector('[data-testid="project-overview-url-status-preview"]')?.textContent).toContain('building');
  });

  it('shows a loopback URL on a networked server as local only, without a Start action', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectOverviewUrlsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { getRegistryWorkspaces: () => of(workspace('Demo', 'PROJ-1')), startProjectUrl: vi.fn() } },
        { provide: ProjectLookupService, useValue: { setWorkspaces: vi.fn() } },
        { provide: ProjectUrlProbeService, useValue: { statusFor: () => 'local-only', refresh: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectOverviewUrlsComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const pill = host.querySelector('[data-testid="project-overview-url-status-preview"]');
    expect(pill?.textContent?.trim()).toBe('local only');
    expect(pill?.getAttribute('data-status')).toBe('local-only');
    expect(pill?.querySelector('[data-testid="status-indicator"]')?.getAttribute('data-tone')).toBe('neutral');
    expect(host.querySelector('[data-testid="project-overview-url-start-preview"]')).toBeNull();
  });

  it('ignores a late registry response from the previous project', async () => {
    const responses = new Map<string, Subject<ReturnType<typeof workspace>>>();
    const getRegistryWorkspaces = vi.fn((name?: string) => {
      const subject = new Subject<ReturnType<typeof workspace>>();
      responses.set(name ?? '', subject);
      return subject;
    });
    await TestBed.configureTestingModule({
      imports: [ProjectOverviewUrlsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { getRegistryWorkspaces: () => getRegistryWorkspaces(currentProject), startProjectUrl: vi.fn() } },
        { provide: ProjectLookupService, useValue: { setWorkspaces: vi.fn() } },
        { provide: ProjectUrlProbeService, useValue: { statusFor: () => 'unknown', refresh: vi.fn() } },
      ],
    }).compileComponents();

    let currentProject = 'First';
    const fixture = TestBed.createComponent(ProjectOverviewUrlsComponent);
    fixture.componentRef.setInput('projectName', currentProject);
    fixture.detectChanges();

    currentProject = 'Second';
    fixture.componentRef.setInput('projectName', currentProject);
    fixture.detectChanges();
    responses.get('Second')!.next(workspace('Second', 'PROJ-2'));
    responses.get('First')!.next(workspace('First', 'PROJ-1'));
    fixture.detectChanges();

    expect(fixture.componentInstance.projectId()).toBe('PROJ-2');
  });
});

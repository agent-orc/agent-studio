import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from '../../../services/auth.service';
import { WorkspaceOverlaysService } from './workspace-overlays.service';
import { FirstRunLaunchService } from './first-run-launch.service';

describe('FirstRunLaunchService', () => {
  const status = signal({ profile: 'networked' });
  const settingsOpen = signal(false);
  let service: FirstRunLaunchService;
  let http: HttpTestingController;

  beforeEach(() => {
    window.location.hash = '';
    status.set({ profile: 'networked' });
    settingsOpen.set(false);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthService, useValue: { status } },
        { provide: WorkspaceOverlaysService, useValue: { settingsOpen } },
      ],
    });
    service = TestBed.inject(FirstRunLaunchService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => { http.verify(); window.location.hash = ''; });

  it('opens only when the live project registry has no active project', () => {
    const open = vi.fn();
    service.maybeOpen(open);
    http.expectOne('/api/v1/projects').flush([{ id: 'PROJ-1', archived: true }]);
    expect(open).toHaveBeenCalledOnce();
  });

  it('keeps an existing project or failed read from implying first run', () => {
    const open = vi.fn();
    service.maybeOpen(open);
    http.expectOne('/api/v1/projects').flush([{ id: 'PROJ-1', archived: false }]);
    service.maybeOpen(open);
    http.expectOne('/api/v1/projects').flush([{ projectId: 'PROJ-2', name: 'Remote', taskKeyPrefix: 'RMT' }]);
    service.maybeOpen(open);
    http.expectOne('/api/v1/projects').flush({}, { status: 503, statusText: 'Unavailable' });
    expect(open).not.toHaveBeenCalled();
  });

  it('respects a requested route and public demo mode', () => {
    const open = vi.fn();
    window.location.hash = '#/projects/demo/board';
    service.maybeOpen(open);
    http.expectOne('/api/v1/projects').flush([]);
    expect(open).not.toHaveBeenCalled();
    status.set({ profile: 'public-demo-readonly' });
    service.maybeOpen(open);
    http.expectNone('/api/v1/projects');
  });
});

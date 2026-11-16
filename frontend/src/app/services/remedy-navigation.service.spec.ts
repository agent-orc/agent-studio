import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RemedyNavigationService } from './remedy-navigation.service';
import { FeatureFlagsService } from './feature-flags.service';
import { TaskReferenceNavigationService } from './task-reference-navigation.service';
import { StudioTabStateService } from '../features/studio-shell/services/studio-tab-state.service';
import { WorkspaceOverlaysService } from '../features/shell/state/workspace-overlays.service';

describe('RemedyNavigationService', () => {
  const overlays = { open: vi.fn(), close: vi.fn() };

  function service(): RemedyNavigationService {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: WorkspaceOverlaysService, useValue: overlays },
        { provide: StudioTabStateService, useValue: { open: vi.fn() } },
        { provide: FeatureFlagsService, useValue: { vsCodeLayout: signal(false) } },
        { provide: TaskReferenceNavigationService, useValue: { openReferenceOrNotify: vi.fn() } },
      ],
    });
    return TestBed.inject(RemedyNavigationService);
  }

  afterEach(() => {
    overlays.open.mockClear();
    window.location.hash = '';
  });

  // AGT-3840: "Open Execution Hosts" led to a list that showed the host Online
  // without the held capability; a way out that names a host opens its row.
  it('opens the named host row focused on its capability', () => {
    window.location.hash = '#/board';
    expect(service().open({
      kind: 'route', target: 'execution-hosts', project: 'demo',
      hostId: 'kori-review-01-runner', capability: 'provider-auth:codex',
    })).toBe(true);

    expect(window.location.hash)
      .toBe('#/workspace/settings/environment?host=kori-review-01-runner&capability=provider-auth%3Acodex');
    expect(overlays.open).not.toHaveBeenCalled();
  });

  it('keeps opening the host list when the action names no host', () => {
    expect(service().open({ kind: 'route', target: 'execution-hosts' })).toBe(true);
    expect(overlays.open).toHaveBeenCalledWith('remote-hosts');
  });
});

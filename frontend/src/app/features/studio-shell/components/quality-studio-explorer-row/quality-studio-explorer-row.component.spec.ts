import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { AuthSessionState } from '../../../../services/auth.service';
import { QualityStudioService, type QualityStudioStatus } from '../../../../services/quality-studio.service';
import { QualityStudioExplorerRowComponent } from './quality-studio-explorer-row.component';

describe('QualityStudioExplorerRowComponent', () => {
  for (const configured of [false, true]) {
    it(configured ? 'shows a configured external row' : 'hides an unconfigured row from viewers', async () => {
      const status: QualityStudioStatus = { state: configured ? 'ready' : 'not-connected',
        configured, owner: false, repositoryId: configured ? 'proj-002' : null };
      const statuses = signal({ Demo: status });
      TestBed.configureTestingModule({
        imports: [QualityStudioExplorerRowComponent],
        providers: [provideZonelessChangeDetection(),
          { provide: AuthSessionState, useValue: { status: signal({ user: { role: 'viewer' } }) } },
          { provide: QualityStudioService, useValue: { status: (name: string) => statuses()[name as 'Demo'], ensure: vi.fn(), open: vi.fn() } },
        ],
      });
      const fixture = TestBed.createComponent(QualityStudioExplorerRowComponent);
      fixture.componentRef.setInput('projectName', 'Demo');
      fixture.componentRef.setInput('displayLabel', 'Demo');
      await fixture.whenStable();
      expect(!!fixture.nativeElement.querySelector('[data-testid="studio-explorer-project-quality-Demo"]')).toBe(configured);
    });
  }
});

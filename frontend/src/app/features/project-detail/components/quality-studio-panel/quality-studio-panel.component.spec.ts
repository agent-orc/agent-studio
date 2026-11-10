import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { AuthSessionState } from '../../../../services/auth.service';
import { QualityStudioService, type QualityStudioState, type QualityStudioStatus } from '../../../../services/quality-studio.service';
import { QualityStudioPanelComponent } from './quality-studio-panel.component';

describe('QualityStudioPanelComponent', () => {
  for (const state of ['not-connected', 'unreachable', 'version-too-old', 'not-registered', 'ready'] as QualityStudioState[]) {
    it(`renders ${state} and gates the jump`, async () => {
      const status: QualityStudioStatus = { state, configured: state !== 'not-connected', owner: true, repositoryId: 'proj-002' };
      const statuses = signal<Record<string, QualityStudioStatus>>({ Demo: status });
      const open = vi.fn();
      TestBed.configureTestingModule({
        imports: [QualityStudioPanelComponent],
        providers: [
          provideZonelessChangeDetection(),
          { provide: AuthSessionState, useValue: { status: signal({ user: { role: 'owner' } }) } },
          { provide: QualityStudioService, useValue: { statuses, status: (name: string) => statuses()[name], ensure: vi.fn(), open } },
        ],
      });
      const fixture = TestBed.createComponent(QualityStudioPanelComponent);
      fixture.componentRef.setInput('projectName', 'Demo');
      await fixture.whenStable();
      const button = fixture.nativeElement.querySelector('[data-testid="quality-studio-open"]') as HTMLButtonElement;
      expect(button.disabled).toBe(state !== 'ready');
      expect(fixture.nativeElement.querySelector('[data-state]')?.getAttribute('data-state')).toBe(state);
      expect(fixture.nativeElement.querySelector('[data-testid="quality-studio-connection-state"]')?.getAttribute('data-tone'))
        .toBe(fixture.componentInstance.stateTone(state));
      if (state === 'ready') { button.click(); expect(open).toHaveBeenCalledWith('Demo'); }
    });
  }
});

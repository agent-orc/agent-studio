import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { CardDeploymentService } from '../../services/card-deployment.service';
import { CardEnvironmentBadgesComponent, cardEnvironmentTone } from './card-environment-badges';
import type { TaskInfo } from '../../../../models/task.model';

describe('CardEnvironmentBadgesComponent', () => {
  it('shows release, deployment time, rollback and not yet in Production', async () => {
    await TestBed.configureTestingModule({ imports: [CardEnvironmentBadgesComponent], providers: [
      provideZonelessChangeDetection(),
      { provide: CardDeploymentService, useValue: { observe: () => undefined, badges: () => [
        { environmentId: 'stage', environmentName: 'Staging', kind: 'staging', state: 'rolled-back',
          releaseId: 'r1', releaseVersion: '1.0', atUtc: '2026-10-16T10:00:00Z' },
        { environmentId: 'prod', environmentName: 'Production', kind: 'production', state: 'not-deployed',
          releaseId: null, releaseVersion: null, atUtc: null },
      ] } },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(CardEnvironmentBadgesComponent);
    fixture.componentRef.setInput('task', { id: 'web-1', key: 'WEB-1', projectName: 'Website' } as TaskInfo);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="card-environment-staging"]')?.textContent?.replace(/\s+/g, ' '))
      .toContain('Staging · Rolled back · 1.0 · 2026-10-16');
    expect(host.querySelector('[data-testid="card-environment-production"]')?.textContent)
      .toContain('Not yet in Production');
    expect(host.querySelector<HTMLElement>('[data-testid="card-environment-staging"]')?.dataset['tone']).toBe('warning');
    expect(host.querySelector<HTMLElement>('[data-testid="card-environment-production"]')?.dataset['tone']).toBe('neutral');
  });

  it('uses product wording and keeps the full date visible for every deployment state', async () => {
    const states = [
      ['failed-rolled-back', 'Failed, rolled back'], ['failed', 'Failed'],
      ['awaiting-approval', 'Approval pending'], ['running', 'Deploying'],
      ['queued', 'Queued'], ['cancelled', 'Cancelled'], ['succeeded', 'Live'],
    ] as const;
    await TestBed.configureTestingModule({ imports: [CardEnvironmentBadgesComponent], providers: [
      provideZonelessChangeDetection(),
      { provide: CardDeploymentService, useValue: { observe: () => undefined, badges: () => states.map(([state], index) => ({
        environmentId: `env-${index}`, environmentName: 'Staging', kind: `stage-${index}`, state,
        releaseId: 'r1', releaseVersion: '2026.11.16.1', atUtc: '2026-11-16T12:00:00Z',
      })) } },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(CardEnvironmentBadgesComponent);
    fixture.componentRef.setInput('task', { id: 'web-1', key: 'WEB-1', projectName: 'Website' } as TaskInfo);
    fixture.detectChanges();
    const badges = fixture.nativeElement.querySelectorAll('[data-testid^="card-environment-"]') as NodeListOf<HTMLElement>;
    states.forEach(([, label], index) => {
      expect(badges[index].textContent).toContain(`Staging · ${label} · 2026.11.16.1 · 2026-11-16`);
      expect(badges[index].classList.contains('status-indicator--wrap')).toBe(true);
    });
  });

  it('maps every card environment state onto a tone', () => {
    const matrix: Record<string, string> = {
      succeeded: 'success', failed: 'danger', 'failed-rolled-back': 'danger', 'rolled-back': 'warning', 'awaiting-approval': 'warning',
      running: 'running', queued: 'neutral', cancelled: 'neutral', 'not-deployed': 'neutral', later: 'neutral',
    };
    for (const [state, tone] of Object.entries(matrix)) expect(cardEnvironmentTone(state)).toBe(tone);
  });
});

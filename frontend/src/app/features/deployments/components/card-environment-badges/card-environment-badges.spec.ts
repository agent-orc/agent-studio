import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { CardDeploymentService } from '../../services/card-deployment.service';
import { CardEnvironmentBadgesComponent } from './card-environment-badges';
import type { TaskInfo } from '../../../../models/task.model';

describe('CardEnvironmentBadgesComponent', () => {
  it('shows release, deployment time, rollback and not yet in Production', async () => {
    await TestBed.configureTestingModule({ imports: [CardEnvironmentBadgesComponent], providers: [
      provideZonelessChangeDetection(),
      { provide: CardDeploymentService, useValue: { badges: () => [
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
      .toContain('Staging · rolled back · 1.0 · 2026-10-16');
    expect(host.querySelector('[data-testid="card-environment-production"]')?.textContent)
      .toContain('Not yet in Production');
  });
});

import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import type { PipelineAdminRow } from '../pipeline-config.util';
import { PipelineModelSettingComponent } from './pipeline-model-setting.component';

describe('PipelineModelSettingComponent', () => {
  it('emits the migration target while showing its cost and reasoning diff', async () => {
    await TestBed.configureTestingModule({
      imports: [PipelineModelSettingComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PipelineModelSettingComponent);
    fixture.componentRef.setInput('sourceLabel', 'step override');
    fixture.componentRef.setInput('step', {
      id: 'review', enabled: true, cliType: 'claude', model: 'claude-sonnet-4-6',
      thinkingLevel: 'medium', effectiveCliType: 'claude', effectiveModel: 'claude-sonnet-4-6',
      effectiveThinkingLevel: 'medium', modelMigration: {
        from: 'claude-sonnet-4-6', to: 'claude-sonnet-5', family: 'claude-sonnet',
        rule: 'latestInFamily:claude-sonnet', catalogVersion: '2026-09-06',
        safeAuto: true, targetAvailable: true, safeAutoCandidate: true,
        costClassFrom: 'standard', costClassTo: 'standard',
        fromReasoningLevels: ['medium'], toReasoningLevels: ['medium', 'high'],
        ladderCompatible: true, note: 'Compatible upgrade.',
      },
    } as PipelineAdminRow);
    fixture.detectChanges();

    let target = '';
    fixture.componentInstance.migrationRequested.subscribe((value) => target = value);
    const host: HTMLElement = fixture.nativeElement;
    expect(host.textContent).toContain('standard to standard');
    host.querySelector<HTMLButtonElement>('[data-testid="pipeline-step-model-migration-review-apply"]')?.click();
    expect(target).toBe('claude-sonnet-5');
  });
});

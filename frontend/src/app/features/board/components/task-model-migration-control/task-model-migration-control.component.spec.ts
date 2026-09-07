import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { ModelMigrationProposal } from '../../../../models/model-migration.model';
import type { TaskInfo } from '../../../../models/task.model';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import type { EffectiveModelChip } from '../task-card/task-card-view-model';
import { TaskModelMigrationControlComponent } from './task-model-migration-control.component';

const migration: ModelMigrationProposal = {
  from: 'claude-sonnet-4-6', to: 'claude-sonnet-5', family: 'claude-sonnet',
  rule: 'latestInFamily:claude-sonnet', catalogVersion: '2026-09-06',
  safeAuto: true, targetAvailable: true, safeAutoCandidate: true,
  costClassFrom: 'standard', costClassTo: 'standard',
  fromReasoningLevels: ['medium'], toReasoningLevels: ['medium', 'high'],
  ladderCompatible: true, note: 'Compatible upgrade.',
};

const task = {
  id: 'job-1', taskKey: 'AGT-1', title: 'Pinned model', state: '1-preparation',
  order: 0, agent: 'claude', createdAt: '', watchPath: '/repo', projectName: 'Demo',
  folderPath: '/tasks/job-1', lastActivity: '', sessionName: null,
  model: migration.from, modelExplicit: true, modelMigration: migration, cliType: 'claude',
} as TaskInfo;

const chip = {
  icon: '', label: 'Sonnet 4.6', fullModel: migration.from, cliType: 'claude',
  cliLabel: 'Claude', source: 'explicit', isDefault: false,
  tooltip: { title: 'Model', body: migration.from }, thinkingLevel: null,
} as EffectiveModelChip;

describe('TaskModelMigrationControlComponent', () => {
  it('applies the offered target through the existing task model mutation', async () => {
    const tasks = { setJobModel: vi.fn(() => of({})), refresh: vi.fn() };
    const notifications = { success: vi.fn(), error: vi.fn() };
    await TestBed.configureTestingModule({
      imports: [TaskModelMigrationControlComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: tasks },
        { provide: NotificationService, useValue: notifications },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskModelMigrationControlComponent);
    fixture.componentRef.setInput('task', task);
    fixture.componentRef.setInput('chip', chip);
    fixture.detectChanges();

    const offer = fixture.nativeElement.querySelector('[data-testid="task-card-model-migration"]');
    expect(offer?.textContent).toContain('claude-sonnet-4-6');
    expect(offer?.getAttribute('aria-label')).toContain('Cost: standard to standard');
    expect(offer?.getAttribute('aria-label')).toContain('Reasoning: medium to medium, high');
    fixture.nativeElement.querySelector('[data-testid="task-card-model-migration-apply"]')?.click();

    expect(tasks.setJobModel).toHaveBeenCalledWith('job-1', 'claude-sonnet-5', '/repo');
    expect(tasks.refresh).toHaveBeenCalledWith(true);
    expect(notifications.success).toHaveBeenCalledWith('Model updated to claude-sonnet-5');
  });
});

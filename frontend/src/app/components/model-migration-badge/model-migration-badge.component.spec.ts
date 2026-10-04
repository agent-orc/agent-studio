import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { ModelMigrationBadgeComponent } from './model-migration-badge.component';
import { ModelMigrationCatalogStore, type ModelMigrationEntry } from '../../features/cli/model-migrations';
import { CliCatalogStore } from '../../features/cli/catalog';
import type { CliModelInfo } from '../../features/cli';
import { NotificationService } from '../../services/notification.service';
import { TaskService } from '../../services/task.service';

/** AGT-2903: proposal-only GPT-6 migration with the mapped-level note on the card badge. */
describe('ModelMigrationBadgeComponent', () => {
  const solMigration: ModelMigrationEntry = {
    from: 'gpt-5.6-sol', to: 'gpt-6-sol', family: 'gpt-sol', safeAuto: false,
    reason: 'Proposal only (TokenEconomy 0.3.6).',
  };
  const lunaMigration: ModelMigrationEntry = {
    from: 'gpt-5.6-luna', to: 'gpt-6-luna', family: 'gpt-luna', safeAuto: false,
    reason: 'Proposal only (TokenEconomy 0.3.6).',
  };
  const codexModels: CliModelInfo[] = [
    { id: 'gpt-6-sol', label: 'GPT-6 Sol', multiplier: null, vendor: 'openai', isDefault: true, thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'], defaultThinkingLevel: 'medium' },
    { id: 'gpt-6-luna', label: 'GPT-6 Luna', multiplier: null, vendor: 'openai', isDefault: false, thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'medium' },
    { id: 'gpt-5.6-sol', label: 'GPT-5.6 Sol', multiplier: null, vendor: 'openai', isDefault: false, thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'], defaultThinkingLevel: 'medium' },
    { id: 'gpt-5.6-luna', label: 'GPT-5.6 Luna', multiplier: null, vendor: 'openai', isDefault: false, thinkingLevels: ['low', 'medium', 'high', 'xhigh'], defaultThinkingLevel: 'medium' },
  ];

  async function create(inputs: Record<string, unknown>) {
    const tasks = {
      setJobModel: vi.fn().mockReturnValue(of({})),
      applyProjectModelMigration: vi.fn().mockReturnValue(of({ from: 'gpt-5.6-sol', to: 'gpt-6-sol', updatedTaskIds: ['a', 'b'], failedTaskIds: [] })),
    };
    const notifications = { success: vi.fn(), error: vi.fn() };
    await TestBed.configureTestingModule({
      imports: [ModelMigrationBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ModelMigrationCatalogStore, useValue: {
          proposalFor: (model: string | null) => [solMigration, lunaMigration].find((m) => m.from === model) ?? null,
        } },
        { provide: CliCatalogStore, useValue: { modelsFor: () => codexModels } },
        { provide: TaskService, useValue: tasks },
        { provide: NotificationService, useValue: notifications },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ModelMigrationBadgeComponent);
    for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
    await fixture.whenStable();
    return { fixture, component: fixture.componentInstance, tasks, notifications };
  }

  const query = (root: HTMLElement, testId: string) => root.querySelector(`[data-testid="${testId}"]`);

  it('offers the gpt-6-sol proposal for a gpt-5.6-sol ultra pin and changes nothing until Apply', async () => {
    const { fixture, component, tasks } = await create({
      model: 'gpt-5.6-sol', explicit: true, jobId: 'job-1', project: 'Studio',
      cliType: 'codex', thinkingLevel: 'ultra', testId: 'card-migration',
    });
    const root = fixture.nativeElement as HTMLElement;

    expect(query(root, 'card-migration-dot')).toBeTruthy();
    // gpt-5.6-sol offers ultra, so the current pin is not mapped.
    expect(query(root, 'card-migration-level-mapped')).toBeNull();
    expect(component.targetLevel()).toEqual({ level: 'ultra', mappedFrom: null });
    expect(tasks.setJobModel).not.toHaveBeenCalled();
    expect(tasks.applyProjectModelMigration).not.toHaveBeenCalled();
  });

  it('shows the mapped level on the badge and in the proposal when discovery does not offer the pin', async () => {
    const { fixture, component } = await create({
      model: 'gpt-5.6-luna', explicit: true, jobId: 'job-2', project: 'Studio',
      cliType: 'codex', thinkingLevel: 'ultra', testId: 'card-migration',
    });
    const root = fixture.nativeElement as HTMLElement;

    const chip = query(root, 'card-migration-level-mapped');
    expect(chip?.textContent?.trim()).toBe('ultra→xhigh');
    expect(chip?.getAttribute('aria-label')).toBe('Pinned ultra is not offered by gpt-5.6-luna; runs at xhigh.');
    expect(component.targetLevel()).toEqual({ level: 'max', mappedFrom: 'ultra' });
  });

  it('applies per card or per project only on an explicit click', async () => {
    const { fixture, component, tasks, notifications } = await create({
      model: 'gpt-5.6-sol', explicit: true, jobId: 'job-1', project: 'Studio',
      cliType: 'codex', thinkingLevel: 'medium', testId: 'card-migration',
    });
    const event = new MouseEvent('click');

    component.onApply(event);
    expect(tasks.setJobModel).toHaveBeenCalledWith('job-1', 'gpt-6-sol', undefined);

    component.onApplyProject(event);
    expect(tasks.applyProjectModelMigration).toHaveBeenCalledWith('Studio', 'gpt-5.6-sol');
    expect(notifications.success).toHaveBeenLastCalledWith('Model updated to gpt-6-sol on 2 cards.');
    fixture.detectChanges();
  });

  it('renders nothing for a non-explicit card', async () => {
    const { fixture } = await create({ model: 'gpt-5.6-sol', explicit: false, cliType: 'codex', thinkingLevel: 'medium' });
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid$="-dot"]')).toBeNull();
  });
});

import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { ModelMigrationBadgeComponent } from './model-migration-badge.component';
import { ModelMigrationCatalogStore } from '../../features/cli/model-migrations';
import { CliCatalogStore } from '../../features/cli';
import { TaskService } from '../../services/task.service';
import { NotificationService } from '../../services/notification.service';
import { OverlayPortalService } from '../../services/overlay-portal.service';

describe('ModelMigrationBadgeComponent', () => {
  it('offers GPT-5.6 Luna migration and applies it only after an operator action', async () => {
    const proposal = { from: 'gpt-5.6-luna', to: 'gpt-6-luna', family: 'gpt-luna',
      safeAuto: false, reason: 'Proposal only.' };
    const tasks = {
      setJobModel: vi.fn().mockReturnValue(of({})),
      applyProjectModelMigration: vi.fn().mockReturnValue(of({ applied: 2 })),
    };
    await TestBed.configureTestingModule({
      imports: [ModelMigrationBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ModelMigrationCatalogStore, useValue: { proposalFor: (model: string) => model === proposal.from ? proposal : null } },
        { provide: CliCatalogStore, useValue: { modelsFor: () => [{ id: 'gpt-6-luna', thinkingLevels: ['medium', 'xhigh', 'max'] }] } },
        { provide: TaskService, useValue: tasks },
        { provide: NotificationService, useValue: { success: vi.fn(), error: vi.fn() } },
        { provide: OverlayPortalService, useValue: { attachPanel: vi.fn(), watchConnectedPosition: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ModelMigrationBadgeComponent);
    fixture.componentRef.setInput('model', proposal.from);
    fixture.componentRef.setInput('thinkingLevel', 'ultra');
    fixture.componentRef.setInput('explicit', true);
    fixture.componentRef.setInput('jobId', 'AGT-123');
    fixture.componentRef.setInput('watchPath', '/workspace/project');
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('[data-testid="model-migration-badge-dot"]')).toBeTruthy();
    expect(fixture.componentInstance.levelMappingNote()).toContain('xhigh');
    expect(tasks.setJobModel).not.toHaveBeenCalled();
    fixture.componentInstance.onApply(new Event('click'));
    expect(tasks.setJobModel).toHaveBeenCalledWith('AGT-123', 'gpt-6-luna', '/workspace/project');
    fixture.componentInstance.onApplyProject(new Event('click'));
    expect(tasks.applyProjectModelMigration).toHaveBeenCalledWith('/workspace/project', 'gpt-5.6-luna', 'gpt-6-luna');
  });
});

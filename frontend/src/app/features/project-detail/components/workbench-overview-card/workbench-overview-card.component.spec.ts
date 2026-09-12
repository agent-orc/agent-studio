import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { WorkbenchOverviewItem } from '../../../../models/project-docs.model';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { WorkbenchOverviewCardComponent } from './workbench-overview-card.component';

const item: WorkbenchOverviewItem = {
  projectName: 'Demo',
  workbench: {
    id: 'long',
    key: 'AGT-W51',
    title: 'Isolated execution',
    summary: 'Long Dossier summary '.repeat(80),
    status: 'decision-pending',
    phase: null,
    updatedAtUtc: '2026-09-12T10:00:00Z',
    entryPath: 'docs/operations/isolated-execution/index.html',
    valid: true,
    error: null,
    sourceTaskKeys: [],
    openDecisionCount: 2,
  },
};

describe('WorkbenchOverviewCardComponent', () => {
  it('owns per-card excerpt expansion and keeps decision actions in its footer', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchOverviewCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskReferenceNavigationService, useValue: { openTaskKey: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchOverviewCardComponent);
    fixture.componentRef.setInput('item', item);
    fixture.componentRef.setInput('project', {
      id: 'PROJ-001', displayName: 'Demo', shortCode: 'DMO', color: '#6c8cff',
      initial: 'D', onColor: '#ffffff', border: '#6c8cff', soft: '#eef2ff',
    });
    fixture.componentRef.setInput('statusLabel', 'Decision pending');
    fixture.componentRef.setInput('variant', 'decision');
    fixture.detectChanges();

    const article = fixture.nativeElement.querySelector('article') as HTMLElement;
    expect(article.children.item(1)?.classList.contains('workbench-overview__footer')).toBe(true);
    expect(article.querySelector('[data-testid="workbench-overview-actions-Demo-long"]')).not.toBeNull();
    const toggle = article.querySelector(
      '[data-testid="workbench-overview-excerpt-toggle-Demo-long"]',
    ) as HTMLButtonElement;
    toggle.click();
    fixture.detectChanges();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(article.querySelector('.workbench-overview__excerpt--expanded')).not.toBeNull();
  });
});

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { WikiLifecycleItem, WikiPulseLifecycle, WorkbenchCatalogue, WorkbenchListItem } from '../../../../../../models/project-docs.model';
import { WorkbenchInboxComponent } from './workbench-inbox.component';
import type { DecisionInboxItem } from '../../../../../../models/decision-card-presentation';

function item(id: string, valid = true): WorkbenchListItem {
  return {
    id,
    key: valid ? `DEM-W${id === 'valid' ? '4' : '5'}` : null,
    title: id,
    summary: `${id} summary`,
    status: valid ? 'active' : 'invalid',
    phase: valid ? 'testing' : null,
    updatedAtUtc: new Date().toISOString(),
    entryPath: `docs/workbenches/${id}/index.html`,
    valid,
    error: valid ? null : 'Descriptor needs repair.',
    sourceTaskKeys: [],
    relatedTaskKeys: [],
  };
}

describe('WorkbenchInboxComponent', () => {
  it('groups lifecycle pages by state and opens Wiki pages or Dossiers directly', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchInboxComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchInboxComponent);
    const valid = item('valid');
    const invalid = item('invalid', false);
    const catalogue: WorkbenchCatalogue = {
      projectName: 'Demo', includesHistory: true, count: 2, items: [valid, invalid],
    };
    const page: WikiLifecycleItem = {
      relPath: 'concepts/indicator.md', title: 'Indicator alternatives', pageKind: 'exploration',
      state: 'review-requested', editedBy: 'Robert', editedAtUtc: new Date().toISOString(),
      history: [], workbenchId: null, valid: true, error: null,
    };
    const workbenchPage: WikiLifecycleItem = {
      relPath: valid.entryPath, title: valid.title, pageKind: 'workbench', state: 'in-progress',
      editedBy: 'Robert', editedAtUtc: valid.updatedAtUtc, history: [], workbenchId: valid.id,
      valid: true, error: null,
    };
    const invalidPage: WikiLifecycleItem = {
      relPath: invalid.entryPath, title: invalid.title, pageKind: 'workbench', state: 'review-requested',
      editedBy: null, editedAtUtc: invalid.updatedAtUtc, history: [], workbenchId: invalid.id,
      valid: false, error: invalid.error,
    };
    const documentedPage: WikiLifecycleItem = {
      relPath: 'concepts/delivery.md', title: 'Delivery record', pageKind: 'concept', state: 'documented',
      editedBy: 'Operator', editedAtUtc: new Date().toISOString(), history: [], workbenchId: null,
      valid: true, error: null,
    };
    const lifecycle: WikiPulseLifecycle = {
      available: true, reason: null, count: 4,
      items: [page, invalidPage, workbenchPage, documentedPage],
    };
    fixture.componentRef.setInput('catalogue', catalogue);
    fixture.componentRef.setInput('lifecycle', lifecycle);
    let openedWorkbench: WorkbenchListItem | null = null;
    let openedPage: WikiLifecycleItem | null = null;
    fixture.componentInstance.openWorkbench.subscribe(value => openedWorkbench = value);
    fixture.componentInstance.openPage.subscribe(value => openedPage = value);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-group-review-requested"]')?.textContent).toContain('Indicator alternatives');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-group-in-progress"]')?.textContent).toContain('valid');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-group-invalid"]')?.textContent).toContain('invalid');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-group-documented"]')?.textContent).toContain('Delivery record');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-group-review-requested"]')?.textContent).not.toContain('Descriptor needs repair.');
    const pageButton = fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-open-concepts/indicator.md"]') as HTMLButtonElement;
    const validButton = fixture.nativeElement.querySelector(
      `[data-testid="project-wiki-lifecycle-open-${valid.entryPath}"]`) as HTMLButtonElement;
    const invalidButton = fixture.nativeElement.querySelector(
      `[data-testid="project-wiki-lifecycle-open-${invalid.entryPath}"]`) as HTMLButtonElement;
    expect(validButton.disabled).toBe(false);
    expect(fixture.nativeElement.querySelector(
      '[data-testid="project-wiki-lifecycle-key-DEM-W4"]')?.textContent).toContain('DEM-W4');
    expect(invalidButton.disabled).toBe(true);
    expect(invalidButton.textContent).toContain('Descriptor needs repair.');
    pageButton.click();
    expect(openedPage).toEqual(page);
    validButton.click();
    expect(openedWorkbench).toEqual(valid);
  });
});

describe('WorkbenchInboxComponent decision cards (AGT-2795)', () => {
  const decisionItem: DecisionInboxItem = {
    taskKey: 'demo::decide-lock-file', key: 'DEM-12', title: 'Stable release contract',
    question: 'Lock file back, or identity without a lock file?', decider: 'Operator',
    dueDate: '2026-09-16T00:00:00Z', overdue: true, blocks: ['DEM-13'],
  };

  async function mount(lifecycle: WikiPulseLifecycle | null, decisions: DecisionInboxItem[]) {
    await TestBed.configureTestingModule({
      imports: [WorkbenchInboxComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchInboxComponent);
    fixture.componentRef.setInput('lifecycle', lifecycle);
    fixture.componentRef.setInput('decisionCards', decisions);
    fixture.detectChanges();
    return fixture;
  }

  it('lists pending decision cards first, beside the Dossier lifecycle, and opens the card', async () => {
    const dossier: WikiLifecycleItem = {
      relPath: 'operations/decision-cards/index.html', title: 'Decision cards', pageKind: 'workbench',
      state: 'in-progress', editedBy: 'Operator', editedAtUtc: new Date().toISOString(), history: [],
      workbenchId: null, valid: true, error: null,
    };
    const fixture = await mount({ available: true, reason: null, count: 1, items: [dossier] }, [decisionItem]);
    const host = fixture.nativeElement as HTMLElement;
    const groups = Array.from(host.querySelectorAll('[data-testid^="project-wiki-lifecycle-group-"]'))
      .map((group) => group.getAttribute('data-testid'));
    expect(groups).toEqual(['project-wiki-lifecycle-group-decision-cards', 'project-wiki-lifecycle-group-in-progress']);
    // Header total equals the rows it lists (R3).
    expect(host.querySelector('.wbinbox__head strong')?.textContent?.trim()).toBe('2');

    const row = host.querySelector('[data-testid="project-wiki-decision-card-DEM-12"]') as HTMLButtonElement;
    expect(row.textContent).toContain('Lock file back, or identity without a lock file?');
    expect(row.textContent).toContain('decider Operator');
    expect(row.textContent).toContain('overdue since 2026-09-16 00:00Z');
    expect(row.textContent).toContain('blocks DEM-13');
    expect(row.getAttribute('data-overdue')).toBe('true');

    const opened: DecisionInboxItem[] = [];
    fixture.componentInstance.openDecisionCard.subscribe((item) => opened.push(item));
    row.click();
    expect(opened).toEqual([decisionItem]);
  });

  it('shows the card for pending decisions even before the lifecycle source loads', async () => {
    const fixture = await mount(null, [decisionItem]);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-wiki-pulse-lifecycle"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="project-wiki-decision-card-DEM-12"]')).not.toBeNull();
  });

  it('renders no card when there is neither a lifecycle nor a pending decision', async () => {
    const fixture = await mount(null, []);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="project-wiki-pulse-lifecycle"]')).toBeNull();
  });
});

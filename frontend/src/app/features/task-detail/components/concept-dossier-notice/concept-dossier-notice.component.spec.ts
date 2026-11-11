import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
import { ConceptDossierNoticeComponent } from './concept-dossier-notice.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { TaskState, type ConceptDossierSummary, type TaskInfo, type TaskMode } from '../../../../models/task.model';

function dossier(overrides: Partial<ConceptDossierSummary> = {}): ConceptDossierSummary {
  return {
    noDossierNeeded: false,
    contractSatisfied: false,
    ...overrides,
  };
}

function job(
  mode: TaskMode = 'concept',
  state: TaskInfo['state'] = TaskState.HumanReview,
  conceptDossier: ConceptDossierSummary | null = dossier(),
): TaskInfo {
  return {
    id: 'concept-card',
    taskKey: 'agt::concept-card',
    key: 'AGT-2548',
    title: 'Concept card',
    state,
    order: 1,
    agent: 'codex',
    createdAt: '2026-08-09T18:00:00Z',
    watchPath: '/workspace/tasks',
    projectName: 'PROJ-002',
    folderPath: '/workspace/tasks/5-human-review/concept-card',
    lastActivity: '2026-08-09T18:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    mode,
    conceptDossier,
  };
}

async function mount(info: TaskInfo, setConceptDossier = vi.fn()) {
  await TestBed.configureTestingModule({
    imports: [ConceptDossierNoticeComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { setConceptDossier } },
      { provide: NotificationService, useValue: { success: vi.fn(), warning: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(ConceptDossierNoticeComponent);
  fixture.componentRef.setInput('job', info);
  fixture.detectChanges();
  return { fixture };
}

describe('ConceptDossierNoticeComponent', () => {
  it('renders exactly one compact missing notice for a concept in review', async () => {
    const { fixture } = await mount(job());
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelectorAll('[data-testid="concept-dossier-notice"]')).toHaveLength(1);
    expect(host.querySelector('[data-testid="concept-dossier-missing"]')?.textContent).toContain('No dossier linked');
    expect(host.querySelector('[data-testid="concept-dossier-add-path"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="concept-dossier-no-need"]')).not.toBeNull();
  });

  it('does not warn before review or on non-concept cards', async () => {
    const beforeReview = await mount(job('concept', TaskState.Progress));
    expect(beforeReview.fixture.nativeElement.querySelector('[data-testid="concept-dossier-notice"]')).toBeNull();
    TestBed.resetTestingModule();

    const coding = await mount(job('coding', TaskState.HumanReview, null));
    expect(coding.fixture.nativeElement.querySelector('[data-testid="concept-dossier-notice"]')).toBeNull();
  });

  it('renders the detected dossier as a clickable Wiki link', async () => {
    const published = job(
      'concept',
      TaskState.Completed,
      dossier({ repoRelativePath: 'docs/coding-agent-sidesheet/index.html', contractSatisfied: true }),
    );
    published.integration = {
      status: 'integrated', deliveryRef: 'refs/results/concept', sha: 'abc123',
      integrationBranch: 'develop', detail: null,
    };
    const { fixture } = await mount(published);
    const link = fixture.nativeElement.querySelector('[data-testid="concept-dossier-link"]') as HTMLAnchorElement;

    expect(link.textContent).toContain('docs/coding-agent-sidesheet/index.html');
    expect(link.getAttribute('href')).toBe(
      '#/projects/proj-002/wiki?page=coding-agent-sidesheet%2Findex.html',
    );
  });

  it('shows the integration stage without a broken Wiki link before publication', async () => {
    const pending = job(
      'concept', TaskState.HumanReview,
      dossier({ repoRelativePath: 'docs/notes-export/index.html', contractSatisfied: true }),
    );
    pending.integration = {
      status: 'pending', deliveryRef: 'refs/results/concept', sha: null,
      integrationBranch: 'develop', detail: null,
    };
    const { fixture } = await mount(pending);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="concept-dossier-link"]')).toBeNull();
    expect(host.querySelector('[data-testid="concept-dossier-path"]')?.textContent)
      .toBe('docs/notes-export/index.html');
    expect(host.querySelector('[data-testid="concept-dossier-publication"]')?.textContent)
      .toContain('Not published yet · Awaiting integration');
  });

  it('records a conscious no-dossier explanation', async () => {
    const saved = dossier({
      noDossierNeeded: true,
      noDossierReason: 'Exploration was discarded.',
      contractSatisfied: true,
    });
    const setConceptDossier = vi.fn(() => of(saved));
    const { fixture } = await mount(job(), setConceptDossier);
    const host = fixture.nativeElement as HTMLElement;

    (host.querySelector('[data-testid="concept-dossier-no-need"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    fixture.componentInstance.reasonDraft.set('Exploration was discarded.');
    fixture.detectChanges();
    (host.querySelector('[data-testid="concept-dossier-no-need-form"]') as HTMLFormElement)
      .dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    expect(setConceptDossier).toHaveBeenCalledWith(
      'concept-card',
      { noDossierNeeded: true, reason: 'Exploration was discarded.' },
      '/workspace/tasks',
    );
    expect(host.querySelector('[data-testid="concept-dossier-no-need-reason"]')?.textContent)
      .toContain('Exploration was discarded.');
  });
});

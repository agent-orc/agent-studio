import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { map } from 'rxjs';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/index';
import type { WorkbenchDocument, WorkbenchPromotion } from '../../../../models/project-docs.model';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import { createDossierCard } from '../../state/dossier-card-creation';
import { actionErrorMessage, readDefaultCli } from '../workbench-decision-panel/workbench-decision-panel.util';

type NextStep = 'create-concept-card' | 'open-concept-card' | null;

/**
 * Stands in for Create implementation cards when a Dossier cannot promote
 * (AGT-3875): it says which piece is missing and offers the next step, so the
 * owner never meets a control that can only fail. A Dossier without a concept
 * card gets one in Preparation; a concept card that has not delivered a
 * reviewed Dossier opens, because its delivery runs the concept review.
 */
@Component({
  selector: 'app-dossier-promotion-notice',
  standalone: true,
  imports: [PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dossier-promotion-notice.component.html',
  styleUrl: './dossier-promotion-notice.component.scss',
})
export class DossierPromotionNoticeComponent {
  readonly projectName = input.required<string>();
  readonly document = input.required<WorkbenchDocument>();
  readonly promotion = input.required<WorkbenchPromotion>();
  readonly readOnly = input(false);

  private readonly tasks = inject(TaskService);
  private readonly navigation = inject(TaskReferenceNavigationService);

  readonly creating = signal(false);
  readonly createdKey = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly nextStep = computed<NextStep>(() => {
    const promotion = this.promotion();
    if (promotion.reason === 'no-concept-card') return 'create-concept-card';
    if (promotion.sourceTaskKey
      && (promotion.reason === 'no-review' || promotion.reason === 'review-failed'
        || promotion.reason === 'other-dossier')) return 'open-concept-card';
    return null;
  });
  readonly nextStepHint = computed(() => {
    switch (this.promotion().reason) {
      case 'no-concept-card':
        return 'A concept card revises this Dossier and its delivery runs the concept review. Then this control creates one card per proposal.';
      case 'no-review':
        return 'Run the concept card. Its delivery runs the concept review of this Dossier.';
      case 'review-failed':
        return 'Ask the concept card for a revision that fixes the review findings.';
      case 'other-dossier':
        return 'Promote from the Dossier the concept card delivered, or link this Dossier to the right concept card.';
      default:
        return null;
    }
  });

  createConceptCard(): void {
    if (this.creating() || this.readOnly()) return;
    const document = this.document();
    const project = this.projectName();
    const cli = readDefaultCli();
    this.creating.set(true);
    this.error.set(null);
    createDossierCard(this.tasks, project, {
      title: `Concept review: ${document.workbench.title}`,
      agent: cli,
      cliType: cli,
      promptMarkdown: conceptCardPrompt(document),
      targetState: '1-preparation',
      mode: 'concept',
      dossier: { path: document.workbench.entryPath },
    }).pipe(map(card => card.key)).subscribe({
      next: key => {
        this.creating.set(false);
        this.createdKey.set(key);
        this.tasks.refresh();
      },
      error: error => {
        this.creating.set(false);
        this.error.set(actionErrorMessage(error));
      },
    });
  }

  openConceptCard(): void {
    const key = this.createdKey() ?? this.promotion().sourceTaskKey;
    if (key) this.navigation.openReferenceOrNotify(key);
  }
}

function conceptCardPrompt(document: WorkbenchDocument): string {
  const workbench = document.workbench;
  return [
    `# Concept review for Dossier ${workbench.key ?? workbench.id}`,
    '',
    `Dossier: \`${workbench.entryPath}\``,
    '',
    'This Dossier was written without a source concept card, so it never passed concept review and cannot create implementation cards.',
    '',
    '## Goal',
    '',
    'Revise the existing Dossier in place through the normal concept delivery path:',
    '',
    '- add this card to `sourceTaskKeys` in `workbench.json`;',
    '- keep the owner\'s recorded answers and notes (`decision.responses` with outcome `record-only`) and treat them as the working decisions;',
    '- make `implementationTasks` validated, workflow-sized proposals so the concept review passes.',
  ].join('\n');
}

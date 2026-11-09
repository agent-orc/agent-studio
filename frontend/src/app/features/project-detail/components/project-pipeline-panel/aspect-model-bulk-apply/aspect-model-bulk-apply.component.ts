import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { Observable, switchMap, throwError } from 'rxjs';
import { QuotaApiService } from '../../../../quota';
import { CLI_TYPES, type CliType } from '../../../../../models/task.model';
import { CliModelSelectorComponent } from '../../../../../components/cli-model-selector';
import { AppTooltipDirective } from '../../../../../components/tooltip/app-tooltip.directive';
import { ConfirmDialogService } from '../../../../../services/confirm-dialog.service';
import type { PipelineAdminRow } from '../pipeline-config.util';

export interface AspectAgentSelection { cliType: CliType; model: string; thinkingLevel: string | null }

/** The workspace consumer class every aspect review step selects through. */
export const ASPECT_CONSUMER = 'review-aspect';

/**
 * Bulk model change for the aspect review group. "Apply to all aspect steps"
 * writes the per-step override of every aspect step in this project and
 * pipeline type through the panel's step route; "Apply to all projects" sets
 * the primary route of the workspace `review-aspect` consumer policy.
 */
@Component({
  selector: 'app-aspect-model-bulk-apply',
  standalone: true,
  imports: [CliModelSelectorComponent, AppTooltipDirective],
  templateUrl: './aspect-model-bulk-apply.component.html',
  styleUrl: './aspect-model-bulk-apply.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AspectModelBulkApplyComponent {
  readonly steps = input.required<readonly PipelineAdminRow[]>();
  /** Writes the selection onto every aspect step override of this project. */
  readonly applyToSteps = input.required<(selection: AspectAgentSelection) => Observable<unknown>>();
  /** Fires after the workspace policy changed, so the panel reloads effective routes. */
  readonly policyApplied = output<void>();

  private readonly api = inject(QuotaApiService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  readonly draft = signal<AspectAgentSelection | null>(null);
  readonly busy = signal<'steps' | 'projects' | null>(null);
  readonly message = signal<{ tone: 'ok' | 'error'; text: string } | null>(null);

  readonly modelSteps = computed(() => this.steps().filter(step => step.usesModel));
  /** The draft starts from the first aspect step's resolved route. */
  readonly selection = computed<AspectAgentSelection | null>(() => {
    const draft = this.draft();
    if (draft) return draft;
    const first = this.modelSteps()[0];
    const cliType = (CLI_TYPES as readonly string[]).includes(first?.effectiveCliType ?? '')
      ? first.effectiveCliType as CliType : null;
    const model = first?.model || first?.effectiveModel || '';
    return cliType && model ? { cliType, model, thinkingLevel: first.effectiveThinkingLevel || null } : null;
  });
  readonly keptOverrides = computed(() =>
    this.modelSteps().filter(step => step.overridesWorkspace).length);

  onSelect(selection: AspectAgentSelection): void {
    this.draft.set(selection);
    this.message.set(null);
  }

  applyToAllSteps(): void {
    const selection = this.selection();
    const count = this.modelSteps().length;
    if (!selection || !count || this.busy()) return;
    this.busy.set('steps');
    this.message.set(null);
    this.applyToSteps()(selection).subscribe({
      error: () => this.finish('error', 'Could not update every aspect step. Reloaded the saved values.'),
      complete: () => this.finish('ok', `${this.routeText(selection)} set on ${count} aspect steps in this project.`),
    });
  }

  async applyToAllProjects(): Promise<void> {
    const selection = this.selection();
    if (!selection || this.busy()) return;
    const ok = await this.confirmDialog.confirm({
      title: 'Apply to all projects?',
      message: `Sets ${this.routeText(selection)} as the workspace route for aspect review steps. `
        + 'Projects and steps with their own override keep it.',
      confirmLabel: 'Apply to all projects',
      kind: 'primary',
    });
    if (!ok) return;
    this.busy.set('projects');
    this.message.set(null);
    this.api.getSelectionPolicy().pipe(switchMap(policy => {
      const setting = policy.consumers[ASPECT_CONSUMER];
      if (!setting) return throwError(() => new Error('missing consumer'));
      return this.api.setConsumerPolicy(ASPECT_CONSUMER, { ...setting, primary:
        { cli: selection.cliType, model: selection.model, thinking: selection.thinkingLevel } });
    })).subscribe({
      next: () => {
        this.policyApplied.emit();
        const kept = this.keptOverrides();
        this.finish('ok', `Workspace aspect route set to ${this.routeText(selection)}.`
          + (kept ? ` ${kept} steps in this project keep their override.` : ''));
      },
      error: () => this.finish('error', 'Could not save the workspace aspect route.'),
    });
  }

  routeText(selection: AspectAgentSelection): string {
    return `${selection.cliType} · ${selection.model}`;
  }

  private finish(tone: 'ok' | 'error', text: string): void {
    this.busy.set(null);
    this.message.set({ tone, text });
  }
}

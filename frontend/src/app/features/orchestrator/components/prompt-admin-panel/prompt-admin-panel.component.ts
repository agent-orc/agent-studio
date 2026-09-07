import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnInit,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { PromptNavSplitterDirective } from './prompt-nav-splitter.directive';
import { PromptCatalogueNavComponent } from '../prompt-catalogue-nav/prompt-catalogue-nav.component';
import { PromptAdminLandingComponent } from '../prompt-admin-landing/prompt-admin-landing.component';
import { PromptReviewSectionComponent } from '../prompt-review-section/prompt-review-section.component';
import { PromptCallTelemetryComponent } from '../prompt-call-telemetry/prompt-call-telemetry.component';
import { PromptCoverageSectionComponent } from '../prompt-coverage-section/prompt-coverage-section.component';
import { PromptMetaSummaryComponent } from '../prompt-meta-summary/prompt-meta-summary.component';
import { GitTelemetryPanelComponent } from '../git-telemetry-panel/git-telemetry-panel.component';
import {
  PromptAdminService,
  PromptDetail,
  PromptPreviewResult,
  PromptProjectOverride,
  PromptReviewRunResponse,
} from '../../../../services/prompt-admin.service';

interface PromptDiffLine {
  kind: 'same' | 'added' | 'removed';
  prefix: string;
  text: string;
}

/**
 * Admin surface for the application-wide system prompts (the
 * `prompts/runtime/*.md` templates). Lists every template with a description
 * of what it steers, shows the shipped default vs the user override, and lets
 * the operator edit (override), reset to default, or re-baseline an override
 * against a changed default. Defaults are never mutated; edits land in a
 * user-data override directory that survives app updates.
 */
@Component({
  selector: 'app-prompt-admin-panel',
  standalone: true,
  imports: [
    FormsModule,
    DatePipe,
    TooltipDirective,
    PromptCatalogueNavComponent,
    PromptNavSplitterDirective,
    PromptAdminLandingComponent,
    PromptReviewSectionComponent,
    PromptCallTelemetryComponent,
    PromptCoverageSectionComponent,
    PromptMetaSummaryComponent,
    GitTelemetryPanelComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-admin-panel.component.html',
  styleUrl: './prompt-admin-panel.component.scss',
})
export class PromptAdminPanelComponent implements OnInit {
  private readonly api = inject(PromptAdminService);
  /** Project scope for the shared catalogue; null renders every origin. */
  readonly projectName = input<string | null>(null);
  private readonly detailScroll = viewChild<ElementRef<HTMLElement>>('detailScroll');
  readonly catalog = this.api.catalog;
  readonly coverage = this.api.coverage;
  readonly loadError = this.api.loadError;

  readonly selectedName = signal<string | null>(null);
  readonly detail = signal<PromptDetail | null>(null);
  readonly draft = signal<string>('');
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  /** Toggles the read-only "shipped default" comparison block. */
  readonly showDefault = signal(false);

  /** Probelauf state: per-slot values keyed by slot name, and the last result. */
  readonly slotValues = signal<Record<string, string>>({});
  readonly previewResult = signal<PromptPreviewResult | null>(null);
  readonly previewBusy = signal(false);
  readonly reviewBusy = signal(false);
  readonly reviewSummary = signal<PromptReviewRunResponse | null>(null);

  readonly dirty = computed(() => {
    const d = this.detail();
    return d != null && this.draft() !== d.effectiveContent;
  });

  readonly diffLines = computed<PromptDiffLine[]>(() => {
    const d = this.detail();
    if (!d?.hasOverride || d.defaultContent == null || d.overrideContent == null) return [];
    return this.buildDiff(d.defaultContent, d.overrideContent);
  });

  ngOnInit(): void {
    void this.init();
  }

  private async init(): Promise<void> {
    await Promise.all([this.api.loadCatalog(), this.api.loadCoverage()]);
  }

  goHome(): void {
    this.selectedName.set(null);
    this.detail.set(null);
    this.actionError.set(null);
    this.scrollDetailToTop();
  }

  async select(name: string): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.actionError.set(null);
    this.showDefault.set(false);
    this.previewResult.set(null);
    this.slotValues.set({});
    try {
      const detail = this.withRegistryArrays(await this.api.getDetail(name));
      this.selectedName.set(name);
      this.detail.set(detail);
      this.draft.set(detail.effectiveContent);
      this.scrollDetailToTop();
    } catch (err: unknown) {
      this.actionError.set(this.describe(err, 'Failed to load prompt'));
    } finally {
      this.busy.set(false);
    }
  }

  private scrollDetailToTop(): void {
    const element = this.detailScroll()?.nativeElement;
    if (!element) return;
    element.scrollTop = 0;
    element.scrollLeft = 0;
  }
  setSlotValue(slot: string, value: string): void {
    this.slotValues.update((v) => ({ ...v, [slot]: value }));
  }

  async runPreview(): Promise<void> {
    const name = this.selectedName();
    if (!name || this.previewBusy()) return;
    this.previewBusy.set(true);
    this.actionError.set(null);
    try {
      // Preview the current draft so unsaved edits are reflected in the Probelauf.
      const result = await this.api.preview(name, this.slotValues(), this.draft());
      this.previewResult.set(result);
    } catch (err: unknown) {
      this.actionError.set(this.describe(err, 'Probelauf failed'));
    } finally {
      this.previewBusy.set(false);
    }
  }

  async reviewCurrent(): Promise<void> {
    const name = this.selectedName();
    if (!name || this.reviewBusy()) return;
    this.reviewBusy.set(true);
    this.actionError.set(null);
    try {
      await this.api.review(name);
      await this.api.loadCatalog();
      const detail = this.withRegistryArrays(await this.api.getDetail(name));
      this.detail.set(detail);
    } catch (err: unknown) {
      this.actionError.set(this.describe(err, 'Prompt review failed'));
    } finally {
      this.reviewBusy.set(false);
    }
  }

  async reviewAll(): Promise<void> {
    if (this.reviewBusy()) return;
    this.reviewBusy.set(true);
    this.actionError.set(null);
    try {
      this.reviewSummary.set(await this.api.reviewAll());
      await this.api.loadCatalog();
    } catch (err: unknown) {
      this.actionError.set(this.describe(err, 'Prompt review failed'));
    } finally {
      this.reviewBusy.set(false);
    }
  }

  async save(): Promise<void> {
    const name = this.selectedName();
    if (!name || this.busy() || !this.dirty()) return;
    await this.run(() => this.api.saveOverride(name, this.draft()));
  }

  async resetToDefault(): Promise<void> {
    const name = this.selectedName();
    if (!name || this.busy()) return;
    await this.run(() => this.api.resetToDefault(name));
  }

  async takeNewDefault(): Promise<void> {
    // "Auf neuen Default": discard the override entirely so the shipped
    // default wins again.
    await this.resetToDefault();
  }

  async keepMine(): Promise<void> {
    // "Behalten": keep the override content, clear the drift banner by
    // re-baselining against the current default.
    const name = this.selectedName();
    if (!name || this.busy()) return;
    await this.run(() => this.api.rebaseline(name));
  }

  openMergeDraft(): void {
    const d = this.detail();
    if (!d?.hasOverride || d.defaultContent == null || d.overrideContent == null) return;
    this.draft.set(this.buildMergeDraft(d));
    this.showDefault.set(true);
  }

  private async run(op: () => Promise<PromptDetail>): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const detail = this.withRegistryArrays(await op());
      this.detail.set(detail);
      this.draft.set(detail.effectiveContent);
      await this.api.loadCatalog();
    } catch (err: unknown) {
      this.actionError.set(this.describe(err, 'Action failed'));
    } finally {
      this.busy.set(false);
    }
  }

  revertDraft(): void {
    const d = this.detail();
    if (d) this.draft.set(d.effectiveContent);
  }

  toggleDefault(): void {
    this.showDefault.update((v) => !v);
  }

  shortSha(sha: string | null): string {
    return sha ? sha.slice(0, 8) : '-';
  }

  detailProjectOverrides(detail: PromptDetail): PromptProjectOverride[] {
    const all = Array.isArray(detail.projectOverrides) ? detail.projectOverrides : [];
    const project = this.projectName();
    return project
      ? all.filter(origin => origin.projectName.localeCompare(
          project,
          undefined,
          { sensitivity: 'accent' },
        ) === 0)
      : all;
  }

  hasAnyDetailOverride(detail: PromptDetail): boolean {
    return detail.hasOverride || this.detailProjectOverrides(detail).length > 0;
  }

  detailOverrideLabel(detail: PromptDetail): string {
    const origins: string[] = [];
    if (detail.hasOverride) origins.push('global');
    for (const project of this.detailProjectOverrides(detail).map(origin => origin.projectName)) {
      if (!origins.includes(project)) origins.push(project);
    }
    return `overridden - ${origins.join(', ')}`;
  }

  staleProjectOverrides(detail: PromptDetail): PromptProjectOverride[] {
    return this.detailProjectOverrides(detail)
      .filter(origin => origin.defaultChangedSinceOverride === true);
  }

  private buildMergeDraft(d: PromptDetail): string {
    const baseLabel = d.baseDefaultSha ? this.shortSha(d.baseDefaultSha) : 'unknown';
    const currentLabel = d.defaultSha ? this.shortSha(d.defaultSha) : 'unknown';
    const baseContent = d.baseDefaultContent
      ?? 'Base default content was not recorded for this older override.';
    return [
      `<<<<<<< Your override, based on default ${baseLabel}`,
      d.overrideContent ?? '',
      `||||||| Default when this override was created, ${baseLabel}`,
      baseContent,
      '=======',
      d.defaultContent ?? '',
      `>>>>>>> Current shipped default, ${currentLabel}`,
      '',
    ].join('\n');
  }

  private buildDiff(defaultText: string, overrideText: string): PromptDiffLine[] {
    const oldLines = this.splitLines(defaultText);
    const newLines = this.splitLines(overrideText);
    if (oldLines.length > 700 || newLines.length > 700) {
      return this.buildCompactDiff(oldLines, newLines);
    }

    const table = Array.from(
      { length: oldLines.length + 1 },
      () => new Uint16Array(newLines.length + 1)
    );
    for (let i = oldLines.length - 1; i >= 0; i--) {
      for (let j = newLines.length - 1; j >= 0; j--) {
        table[i][j] = oldLines[i] === newLines[j]
          ? table[i + 1][j + 1] + 1
          : Math.max(table[i + 1][j], table[i][j + 1]);
      }
    }

    const lines: PromptDiffLine[] = [];
    let i = 0;
    let j = 0;
    while (i < oldLines.length && j < newLines.length) {
      if (oldLines[i] === newLines[j]) {
        lines.push({ kind: 'same', prefix: ' ', text: oldLines[i] });
        i++;
        j++;
      } else if (table[i + 1][j] >= table[i][j + 1]) {
        lines.push({ kind: 'removed', prefix: '-', text: oldLines[i++] });
      } else {
        lines.push({ kind: 'added', prefix: '+', text: newLines[j++] });
      }
    }
    while (i < oldLines.length) lines.push({ kind: 'removed', prefix: '-', text: oldLines[i++] });
    while (j < newLines.length) lines.push({ kind: 'added', prefix: '+', text: newLines[j++] });
    return lines;
  }

  private buildCompactDiff(oldLines: string[], newLines: string[]): PromptDiffLine[] {
    let prefix = 0;
    while (
      prefix < oldLines.length &&
      prefix < newLines.length &&
      oldLines[prefix] === newLines[prefix]
    ) {
      prefix++;
    }

    let suffix = 0;
    while (
      suffix < oldLines.length - prefix &&
      suffix < newLines.length - prefix &&
      oldLines[oldLines.length - suffix - 1] === newLines[newLines.length - suffix - 1]
    ) {
      suffix++;
    }

    const lines: PromptDiffLine[] = [];
    for (let i = 0; i < prefix; i++) lines.push({ kind: 'same', prefix: ' ', text: oldLines[i] });
    for (let i = prefix; i < oldLines.length - suffix; i++) {
      lines.push({ kind: 'removed', prefix: '-', text: oldLines[i] });
    }
    for (let i = prefix; i < newLines.length - suffix; i++) {
      lines.push({ kind: 'added', prefix: '+', text: newLines[i] });
    }
    for (let i = oldLines.length - suffix; i < oldLines.length; i++) {
      lines.push({ kind: 'same', prefix: ' ', text: oldLines[i] });
    }
    return lines;
  }

  private splitLines(text: string): string[] {
    return text.replace(/\r\n/g, '\n').split('\n');
  }

  /** Older prompt registries did not return these read-only metadata arrays.
   *  Normalize at the API boundary so opening System prompts cannot escape as
   *  a global template error while a backend and frontend version overlap. */
  private withRegistryArrays(detail: PromptDetail): PromptDetail {
    return {
      ...detail,
      slots: Array.isArray(detail.slots) ? detail.slots : [],
      usages: Array.isArray(detail.usages) ? detail.usages : [],
      projectOverrides: Array.isArray(detail.projectOverrides) ? detail.projectOverrides : [],
      costDisclaimer: detail.costDisclaimer ?? '',
      calls: detail.calls ?? {
        totalCalls: 0,
        calls7d: 0,
        lastCalledAt: null,
        inputTokens: 0,
        costUsd: 0,
        costUsd7d: 0,
        unpricedCalls: 0,
        unpricedCalls7d: 0,
        currentVersionCalls: 0,
        isDead: true,
        daily: [],
        versions: [],
      },
      review: detail.review
        ? {
            ...detail.review,
            findings: Array.isArray(detail.review.findings) ? detail.review.findings : [],
          }
        : null,
    };
  }

  private describe(err: unknown, fallback: string): string {
    if (err && typeof err === 'object') {
      const e = err as { error?: { error?: string }; message?: string };
      if (e.error?.error) return e.error.error;
      if (e.message) return e.message;
    }
    return fallback;
  }
}

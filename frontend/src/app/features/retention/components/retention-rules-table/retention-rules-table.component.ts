import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  MIB,
  PLATFORM_RETENTION_RULES,
  formatRetentionBytes,
  sameRetentionRule,
  type RetentionArtifactClass,
  type RetentionPolicy,
  type RetentionRule,
} from '../../models/retention.model';
import { RetentionService } from '../../services/retention.service';

export type RetentionRuleScope = 'workspace' | 'project';
type RuleStage = 'keep' | 'evidence' | 'full' | 'thinned' | 'stub' | 'cold-delete' | 'runtime';

interface RuleRow {
  id: RuleStage;
  artifactClass: RetentionArtifactClass;
  classLabel: string;
  stageLabel: string;
  contents: string;
}

const ROWS: readonly RuleRow[] = [
  { id: 'keep', artifactClass: 'Authority', classLabel: 'A · Authority', stageLabel: 'Keep', contents: 'Task state, events, leases, fences, reviews, audit, and chat turns.' },
  { id: 'evidence', artifactClass: 'Evidence', classLabel: 'B · Evidence', stageLabel: 'With task', contents: 'Status, prompts, reviews, integration records, session events, and reports.' },
  { id: 'full', artifactClass: 'HeavyWorkingData', classLabel: 'C · Heavy data', stageLabel: '0 · Full', contents: 'CLI and review logs, results, traces, screenshots, and attachments remain hot.' },
  { id: 'thinned', artifactClass: 'HeavyWorkingData', classLabel: 'C · Heavy data', stageLabel: '1 · Thinned', contents: 'Originals move cold; content-aware Markdown excerpts remain hot.' },
  { id: 'stub', artifactClass: 'HeavyWorkingData', classLabel: 'C · Heavy data', stageLabel: '2 · Stub', contents: 'Task metadata, status, excerpts, and the archive manifest remain hot.' },
  { id: 'cold-delete', artifactClass: 'HeavyWorkingData', classLabel: 'C · Heavy data', stageLabel: '3 · Optional deletion', contents: 'Cold payload is deleted only when explicitly enabled; the tombstone remains.' },
  { id: 'runtime', artifactClass: 'Runtime', classLabel: 'D · Runtime', stageLabel: 'Delete', contents: 'Bus logs, attempt-authority day archives, rotations, caches, and temporary files.' },
];

@Component({
  selector: 'app-retention-rules-table',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './retention-rules-table.component.html',
  styleUrl: './retention-rules-table.component.scss',
})
export class RetentionRulesTableComponent implements OnInit {
  readonly scope = input<RetentionRuleScope>('workspace');
  readonly projectId = input<string | null>(null);

  private readonly retention = inject(RetentionService);
  readonly workspacePolicy = signal<RetentionPolicy | null>(null);
  readonly projectPolicy = signal<RetentionPolicy | null>(null);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly editingRow = signal<RuleStage | null>(null);
  readonly draft = signal<RetentionRule | null>(null);
  readonly rows = ROWS;
  readonly isProject = computed(() => this.scope() === 'project');

  ngOnInit(): void {
    void this.reload();
  }

  async reload(conflict = false): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const workspace = await firstValueFrom(this.retention.getWorkspacePolicy());
      this.workspacePolicy.set(workspace);
      if (this.isProject()) {
        const projectId = this.projectId();
        if (!projectId) throw new Error('Project retention needs a registered project id.');
        this.projectPolicy.set(await firstValueFrom(this.retention.getProjectPolicy(projectId)));
      }
      if (conflict) this.message.set('The policy changed elsewhere. The newer version is loaded; review it before editing again.');
    } catch (error: unknown) {
      this.error.set(this.errorMessage(error, 'Retention rules could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  ruleFor(artifactClass: RetentionArtifactClass): RetentionRule | null {
    const projectRule = this.projectPolicy()?.rules.find(rule => rule.artifactClass === artifactClass);
    return projectRule
      ?? this.workspacePolicy()?.rules.find(rule => rule.artifactClass === artifactClass)
      ?? null;
  }

  sourceLabel(row: RuleRow): string {
    if (this.isProject()) return this.hasProjectOverride(row.artifactClass) ? 'Project override' : 'Workspace default';
    const current = this.ruleFor(row.artifactClass);
    const platform = PLATFORM_RETENTION_RULES.find(rule => rule.artifactClass === row.artifactClass);
    return current && platform && sameRetentionRule(current, { ...platform, neverArchiveLanes: current.neverArchiveLanes })
      ? 'Platform default'
      : 'Workspace rule';
  }

  hasProjectOverride(artifactClass: RetentionArtifactClass): boolean {
    return !!this.projectPolicy()?.rules.some(rule => rule.artifactClass === artifactClass);
  }

  beginEdit(row: RuleRow): void {
    const rule = this.ruleFor(row.artifactClass);
    if (!rule || row.id === 'keep') return;
    this.message.set(null);
    this.error.set(null);
    this.editingRow.set(row.id);
    this.draft.set({ ...rule, neverArchiveLanes: [...rule.neverArchiveLanes] });
  }

  cancelEdit(): void {
    this.editingRow.set(null);
    this.draft.set(null);
  }

  setMib(field: 'hotCapBytesPerFile' | 'hotBudgetBytesPerTask' | 'refuseAboveBytes', value: string): void {
    this.patchDraft({ [field]: Math.round(Number(value) * MIB) });
  }

  setDays(
    field: 'archiveAfterDaysTerminal' | 'archiveTaskAfterDaysTerminal' | 'deleteAfterDays' | 'deleteArchiveAfterDaysTerminal',
    value: string,
  ): void {
    const parsed = Number(value);
    this.patchDraft({ [field]: Number.isFinite(parsed) ? Math.round(parsed) : null });
  }

  setColdDeleteEnabled(enabled: boolean): void {
    this.patchDraft({ deleteArchiveEnabled: enabled });
  }

  private patchDraft(patch: Partial<RetentionRule>): void {
    const current = this.draft();
    if (current) this.draft.set({ ...current, ...patch });
  }

  validationError(row: RuleRow): string | null {
    const rule = this.draft();
    if (!rule || this.editingRow() !== row.id) return null;
    if (row.id === 'full') {
      if (rule.hotCapBytesPerFile < MIB) return 'File cap must be at least 1 MiB.';
      if (rule.hotBudgetBytesPerTask < rule.hotCapBytesPerFile) return 'Task budget must be at least the file cap.';
      if (rule.refuseAboveBytes < rule.hotCapBytesPerFile) return 'Refusal limit must be at least the file cap.';
      if (rule.refuseAboveBytes > 95 * MIB) return 'Refusal limit cannot exceed 95 MiB.';
    }
    const days = row.id === 'thinned' ? rule.archiveAfterDaysTerminal
      : row.id === 'stub' || row.id === 'evidence' ? rule.archiveTaskAfterDaysTerminal
        : row.id === 'runtime' ? rule.deleteAfterDays
          : row.id === 'cold-delete' && rule.deleteArchiveEnabled ? rule.deleteArchiveAfterDaysTerminal
            : null;
    return days != null && days < 7 ? 'Retention periods must be at least 7 days.' : null;
  }

  async save(row: RuleRow): Promise<void> {
    const draft = this.draft();
    if (!draft || this.validationError(row) || this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    this.message.set(null);
    try {
      if (this.isProject()) {
        const projectId = this.projectId()!;
        const policy = this.projectPolicy()!;
        const rules = policy.rules.filter(rule => rule.artifactClass !== draft.artifactClass);
        rules.push(draft);
        this.projectPolicy.set(await firstValueFrom(this.retention.updateProjectPolicy(projectId, {
          rules,
          expectedVersion: policy.version,
        })));
      } else {
        const policy = this.workspacePolicy()!;
        const rules = policy.rules.map(rule => rule.artifactClass === draft.artifactClass ? draft : rule);
        this.workspacePolicy.set(await firstValueFrom(this.retention.updateWorkspacePolicy({
          rules,
          expectedVersion: policy.version,
          fullBackups: policy.fullBackups,
        })));
      }
      this.cancelEdit();
      this.message.set('Retention rule saved.');
    } catch (error: unknown) {
      if (error instanceof HttpErrorResponse && error.status === 409) {
        this.cancelEdit();
        await this.reload(true);
      } else {
        this.error.set(this.errorMessage(error, 'The retention rule could not be saved.'));
      }
    } finally {
      this.saving.set(false);
    }
  }

  async reset(row: RuleRow): Promise<void> {
    if (!this.isProject() || !this.hasProjectOverride(row.artifactClass) || this.saving()) return;
    const projectId = this.projectId()!;
    const policy = this.projectPolicy()!;
    const rules = policy.rules.filter(rule => rule.artifactClass !== row.artifactClass);
    this.saving.set(true);
    this.error.set(null);
    try {
      if (rules.length === 0) {
        await firstValueFrom(this.retention.resetProjectPolicy(projectId, policy.version));
        this.projectPolicy.set({ ...policy, version: 0, updatedBy: 'workspace-default', rules: [] });
      } else {
        this.projectPolicy.set(await firstValueFrom(this.retention.updateProjectPolicy(projectId, {
          rules,
          expectedVersion: policy.version,
        })));
      }
      this.cancelEdit();
      this.message.set(`${row.classLabel} reset to the workspace default.`);
    } catch (error: unknown) {
      if (error instanceof HttpErrorResponse && error.status === 409) await this.reload(true);
      else this.error.set(this.errorMessage(error, 'The project override could not be reset.'));
    } finally {
      this.saving.set(false);
    }
  }

  mib(bytes: number): number {
    return Math.round((bytes / MIB) * 10) / 10;
  }

  bytes(bytes: number): string {
    return bytes > 0 ? formatRetentionBytes(bytes) : '–';
  }

  days(value: number | null): string {
    return value == null ? '–' : `${value} days`;
  }

  private errorMessage(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) return error.error?.message ?? error.error?.error ?? fallback;
    return error instanceof Error ? error.message : fallback;
  }
}

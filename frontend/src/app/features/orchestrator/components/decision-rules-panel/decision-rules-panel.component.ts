import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';

interface Rule {
  id: string;
  trigger: string;
  factId: string;
  warningThreshold: string | null;
  candidateActions: string[];
  autonomyClass: string;
  model: string | null;
  missingDataBehavior: string;
  freshnessMinutes: number;
}

interface Version {
  versionId: string;
  parentVersionId: string | null;
  actor: string;
  reason: string;
  diff: string;
  createdAt: string;
  rules: Rule[];
}

interface RuleView {
  activeVersionId: string | null;
  versions: Version[];
  hardFloors: { id: string; scope: string; value: string; source: string; owner: string; version: string }[];
}

interface Replay {
  replayed: number;
  floorConflicts: string[];
  occurrences: { receiptId: string; occurrenceId: string; oldSignals: string[]; newSignals: string[];
    oldCandidates: string[]; newCandidates: string[]; observedOutcome: string;
    missingEvidence: boolean; modelResultLabel: string }[];
}

interface HostReceipt {
  receiptId: string;
  taskId: string | null;
  hostId: string | null;
  profile: string;
  chosenAction: string;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  estimatedUsd: number | null;
  evidenceRef: string;
}

interface Spend {
  occurrences: number;
  inputTokens: number;
  outputTokens: number;
  pricedUsd: number;
  unknownPriceCalls: number;
}

@Component({
  selector: 'app-decision-rules-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './decision-rules-panel.component.html',
  styleUrl: './decision-rules-panel.component.scss',
})
export class DecisionRulesPanelComponent implements OnInit {
  private readonly http = inject(HttpClient);
  readonly workspaces = signal<RegistryWorkspaceListItem[]>([]);
  readonly workspaceId = signal('');
  readonly selectedProjects = computed(() => this.workspaces().find(w => w.id === this.workspaceId())?.projects ?? []);
  readonly projectId = signal('');
  readonly view = signal<RuleView | null>(null);
  readonly rules = signal<Rule[]>([]);
  readonly inherited = signal<Rule[]>([]);
  readonly reason = signal('');
  readonly draftId = signal<string | null>(null);
  readonly replay = signal<Replay | null>(null);
  readonly hostHistory = signal<HostReceipt[]>([]);
  readonly spend = signal<Spend | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  ngOnInit(): void { void this.loadWorkspaces(); }

  private get url(): string {
    const scope = this.projectId() ? `?projectId=${encodeURIComponent(this.projectId())}` : '';
    return `/api/workspaces/${encodeURIComponent(this.workspaceId())}/decision-rules${scope}`;
  }

  private versionUrl(id: string, action: string): string {
    const base = `/api/workspaces/${encodeURIComponent(this.workspaceId())}/decision-rules/versions/${encodeURIComponent(id)}/${action}`;
    return base + (this.projectId() ? `?projectId=${encodeURIComponent(this.projectId())}` : '');
  }

  async loadWorkspaces(): Promise<void> {
    try {
      const rows = await firstValueFrom(this.http.get<RegistryWorkspaceListItem[]>('/api/workspaces'));
      this.workspaces.set(rows);
      if (rows.length) { this.workspaceId.set(rows[0].id); await this.refresh(); }
    } catch { this.error.set('Could not load workspaces.'); }
  }

  async selectWorkspace(id: string): Promise<void> {
    this.workspaceId.set(id);
    this.projectId.set('');
    await this.refresh();
  }

  async selectProject(id: string): Promise<void> {
    this.projectId.set(id);
    await this.refresh();
  }

  async refresh(): Promise<void> {
    if (!this.workspaceId()) return;
    try {
      const view = await firstValueFrom(this.http.get<RuleView>(this.url));
      const workspaceView = this.projectId()
        ? await firstValueFrom(this.http.get<RuleView>(
          `/api/workspaces/${encodeURIComponent(this.workspaceId())}/decision-rules`)) : null;
      this.inherited.set(workspaceView?.versions.find(v =>
        v.versionId === workspaceView.activeVersionId)?.rules ?? []);
      const workspaceQuery = `workspaceId=${encodeURIComponent(this.workspaceId())}`;
      const [receipts, spend] = await Promise.all([
        firstValueFrom(this.http.get<HostReceipt[]>(
          `/api/orchestration/decision-receipts?${workspaceQuery}&limit=20`)).catch(() => []),
        firstValueFrom(this.http.get<Spend>(
          `/api/orchestration/decision-receipts/spend?${workspaceQuery}`)).catch(() => null),
      ]);
      this.hostHistory.set(receipts.filter(row => row.taskId === null));
      this.spend.set(spend);
      this.view.set(view);
      this.rules.set(view.versions.find(v => v.versionId === view.activeVersionId)?.rules.map(r => ({ ...r })) ?? []);
      this.draftId.set(null);
      this.replay.set(null);
      this.error.set(null);
    } catch { this.error.set('Could not load decision rules.'); }
  }

  addRule(): void {
    this.rules.update(rows => [...rows, {
      id: '', trigger: 'capacity-check', factId: '', warningThreshold: null,
      candidateActions: ['observe'], autonomyClass: 'A1', model: 'gpt-5.6-sol',
      missingDataBehavior: 'ask', freshnessMinutes: 60,
    }]);
  }

  updateRule(index: number, field: keyof Rule, value: string): void {
    this.rules.update(rows => rows.map((row, i) => i !== index ? row : {
      ...row,
      [field]: field === 'candidateActions' ? value.split(',').map(v => v.trim()).filter(Boolean)
        : field === 'freshnessMinutes' ? Number(value) : value,
    }));
  }

  removeRule(index: number): void { this.rules.update(rows => rows.filter((_, i) => i !== index)); }

  isOverridden(id: string): boolean { return this.rules().some(rule => rule.id === id); }

  async draft(): Promise<void> {
    if (!this.reason().trim() || this.busy()) return;
    this.busy.set(true);
    try {
      const version = await firstValueFrom(this.http.post<Version>(
        `/api/workspaces/${encodeURIComponent(this.workspaceId())}/decision-rules/versions`
          + (this.projectId() ? `?projectId=${encodeURIComponent(this.projectId())}` : ''),
        { parentVersionId: this.view()?.activeVersionId ?? null, actor: '',
          reason: this.reason().trim(), rules: this.rules() }));
      this.draftId.set(version.versionId);
      this.view.update(view => view ? { ...view, versions: [...view.versions, version] } : view);
      this.error.set(null);
    } catch (error) { this.error.set(this.message(error)); }
    finally { this.busy.set(false); }
  }

  async activate(id: string): Promise<void> { await this.changeVersion(id, 'activate'); }
  async rollback(id: string): Promise<void> { await this.changeVersion(id, 'rollback'); }

  private async changeVersion(id: string, action: 'activate' | 'rollback'): Promise<void> {
    if (!this.reason().trim() || this.busy()) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.http.post(this.versionUrl(id, action),
        { actor: '', reason: this.reason().trim() }));
      await this.refresh();
    } catch (error) { this.error.set(this.message(error)); }
    finally { this.busy.set(false); }
  }

  async dryRun(id: string): Promise<void> {
    try {
      this.replay.set(await firstValueFrom(this.http.get<Replay>(this.versionUrl(id, 'dry-run'))));
      this.error.set(null);
    } catch (error) { this.error.set(this.message(error)); }
  }

  private message(error: unknown): string {
    const failure = error as { error?: { reasons?: string[]; reason?: string } };
    return failure.error?.reasons?.join(' ') ?? failure.error?.reason ?? 'Rule operation failed.';
  }
}

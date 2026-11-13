import { DatePipe, DecimalPipe } from '@angular/common';
import { LoadingSurfaceComponent, PendingButtonDirective } from '../../../components/async-feedback';
import { StatusIndicatorComponent } from '../../../components/status-indicator/status-indicator.component';
import { SegmentedControlComponent } from '../../../components/segmented-control/segmented-control.component';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { firstValueFrom, forkJoin } from 'rxjs';
import { AuthService } from '../../../services/auth.service';
import { getPasskey } from '../../../services/webauthn';
import type { RegistryWorkspaceListItem } from '../../../models/task.model';
import { StudioTabStateService } from '../../studio-shell/services/studio-tab-state.service';
import { OrchestratorPanelStateService } from '../../orchestrator/state/orchestrator-panel-state.service';
import { PersonaBudget, PersonaEntry, PersonaItem, PersonaReport, isOpenPersonaItem, personaCards, poolTotals } from './persona-team.model';

@Component({ selector: 'app-persona-team-view', standalone: true,
  imports: [DatePipe, DecimalPipe, LoadingSurfaceComponent, PendingButtonDirective, StatusIndicatorComponent, SegmentedControlComponent], templateUrl: './persona-team-view.html', styleUrl: './persona-team-view.scss',
  changeDetection: ChangeDetectionStrategy.OnPush })
export class PersonaTeamViewComponent {
  readonly personaId = input<string | null>(null);
  readonly workspaces = input<readonly RegistryWorkspaceListItem[]>([]);
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly tabs = inject(StudioTabStateService);
  private readonly chat = inject(OrchestratorPanelStateService);
  readonly isOpenPersonaItem = isOpenPersonaItem;
  readonly workspaceId = signal('');
  readonly loading = signal(false);
  readonly error = signal('');
  readonly busy = signal('');
  readonly stepUpCode = signal('');
  readonly entries = signal<PersonaEntry[]>([]);
  readonly items = signal<PersonaItem[]>([]);
  readonly reports = signal<PersonaReport[]>([]);
  readonly budget = signal<PersonaBudget | null>(null);
  readonly tabOptions = [{ value: 'feed', label: 'Feed' }, { value: 'reports', label: 'Reports' },
    { value: 'proposals', label: 'Proposals' }, { value: 'budget', label: 'Budget' },
    { value: 'charter', label: 'Charter' }, { value: 'history', label: 'History' }] as const;
  readonly tab = signal<'feed' | 'reports' | 'proposals' | 'budget' | 'charter' | 'history'>('feed');
  readonly sort = signal<'importance' | 'time'>('importance');
  readonly kind = signal('all');
  readonly state = signal('open');
  readonly cards = computed(() => personaCards(this.entries(), this.budget(), this.items(), this.reports()));
  readonly totals = computed(() => poolTotals(this.cards(), this.budget()?.poolTokens ?? 0));
  readonly current = computed(() => this.entries().find(entry => entry.id === this.personaId()) ?? null);
  readonly currentCard = computed(() => this.cards().find(card => card.entry.id === this.personaId()) ?? null);
  readonly feed = computed(() => this.items().filter(item => item.personaId === this.personaId()
    && (this.kind() === 'all' || item.kind === this.kind())
    && (this.state() === 'all' || (this.state() === 'open' ? isOpenPersonaItem(item) : item.state === this.state())))
    .sort((a, b) => this.sort() === 'importance'
      ? b.importance - a.importance || a.createdAtUtc.localeCompare(b.createdAtUtc)
      : b.createdAtUtc.localeCompare(a.createdAtUtc)));
  readonly personaReports = computed(() => this.reports().filter(report => report.personaId === this.personaId())
    .sort((a, b) => b.publishedAtUtc.localeCompare(a.publishedAtUtc)));
  readonly proposals = computed(() => this.items().filter(item => item.personaId === this.personaId() && item.kind === 'proposal'));
  readonly roleGroups = computed(() => {
    const groups = new Map<string, ReturnType<typeof personaCards>>();
    for (const card of this.cards()) {
      const role = card.entry.activation?.roleId || card.entry.id;
      groups.set(role, [...(groups.get(role) ?? []), card]);
    }
    return [...groups].map(([role, cards]) => ({ role, cards }));
  });
  constructor() {
    effect(() => {
      const workspaces = this.workspaces();
      if (!this.workspaceId() && workspaces.length) this.selectWorkspace(workspaces[0].id);
    });
  }
  selectWorkspace(id: string): void { this.workspaceId.set(id); this.load(); }
  load(): void {
    const id = this.workspaceId(); if (!id) return;
    this.loading.set(true); this.error.set('');
    const root = `/api/workspaces/${encodeURIComponent(id)}/personas`;
    forkJoin({ list: this.http.get<{ personas: PersonaEntry[] }>(root),
      budget: this.http.get<PersonaBudget>(`${root}/budget`),
      items: this.http.get<PersonaItem[]>(`${root}/items`),
      reports: this.http.get<PersonaReport[]>(`${root}/reports`) }).subscribe({
      next: data => { this.entries.set(data.list.personas); this.budget.set(data.budget);
        this.items.set(data.items); this.reports.set(data.reports); this.loading.set(false); },
      error: () => { this.error.set('Team data could not be loaded.'); this.loading.set(false); },
    });
  }
  open(id: string | null): void { this.tabs.open({ kind: 'team', personaId: id }); this.tab.set('feed'); }
  async action(path: string, key: string, rollback?: () => void, requiresStepUp = false): Promise<void> {
    if (!this.workspaceId()) return;
    this.busy.set(key); this.error.set('');
    try {
      if (requiresStepUp) {
        const code = this.stepUpCode().trim();
        if (code) await firstValueFrom(this.auth.stepUp(code));
        else {
          const options = await firstValueFrom(this.auth.passkeyStepUpOptions());
          await firstValueFrom(this.auth.stepUp(undefined, await getPasskey(options)));
        }
        this.stepUpCode.set('');
      }
      await firstValueFrom(this.http.post(`/api/workspaces/${encodeURIComponent(this.workspaceId())}/personas/${path}`, {}));
      this.load();
    } catch {
      rollback?.(); this.error.set(`${key} failed. The previous state has been restored.`);
    } finally { this.busy.set(''); }
  }
  async decide(item: PersonaItem, outcome: 'accepted' | 'rejected' | 'deferred' | 'resolved'): Promise<void> {
    const before = this.items();
    this.items.set(before.map(row => row.id === item.id ? { ...row, state: outcome } : row));
    this.busy.set(item.id); this.error.set('');
    try {
      await firstValueFrom(this.http.post(`/api/workspaces/${encodeURIComponent(this.workspaceId())}/personas/items/${encodeURIComponent(item.id)}/decision`, { outcome }));
      this.load();
    } catch {
      this.items.set(before); this.error.set('The decision failed. The previous item state has been restored.');
    } finally { this.busy.set(''); }
  }
  stopAll(): void {
    const before = this.entries();
    this.entries.set(before.map(entry => entry.activation && entry.activation.state !== 'inactive'
      ? { ...entry, activation: { ...entry.activation, state: 'inactive' } } : entry));
    void this.action('stop-all', 'Stop all personas', () => this.entries.set(before));
  }
  change(entry: PersonaEntry, verb: string): void {
    void this.action(`${encodeURIComponent(entry.id)}/${verb}`, verb, undefined, verb === 'activate' || verb === 'adopt');
  }
  /**
   * Opens the in-app chat on this tab. The composer's location context for a
   * persona tab carries the reference `persona:<id>` (composer-location-context.ts).
   */
  ask(): void { this.chat.setOpen(true); }
  percent(used: number, allowance: number): number { return allowance ? Math.min(100, used / allowance * 100) : 0; }
  roleLabel(role: string): string { return role.replace(/[-_]/g, ' ').replace(/\b\w/g, letter => letter.toUpperCase()); }
}

import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { forkJoin } from 'rxjs';
import { LoadingSurfaceComponent, PendingButtonDirective } from '../../../components/async-feedback';
import { SegmentedControlComponent } from '../../../components/segmented-control/segmented-control.component';
import { StatusIndicatorComponent } from '../../../components/status-indicator/status-indicator.component';
import { AuthService } from '../../../services/auth.service';
import { RegistryWorkspaceListItem } from '../../../models/task.model';
import { StudioTabStateService } from '../../studio-shell/services/studio-tab-state.service';
import { PersonaEntry } from '../persona-team-view/persona-team.model';
import { Criterion, DispatchConflict, DispatchReview, DispatchRow, defaultWeights, dispatchCriteria, dispatchRoute, validWeights, WeightRevision } from './dispatch-review.model';

@Component({ selector: 'app-dispatch-review', standalone: true,
  imports: [DatePipe, LoadingSurfaceComponent, PendingButtonDirective, SegmentedControlComponent, StatusIndicatorComponent],
  templateUrl: './dispatch-review.html', styleUrl: './dispatch-review.scss', changeDetection: ChangeDetectionStrategy.OnPush })
export class DispatchReviewComponent {
  readonly workspaces = input<readonly RegistryWorkspaceListItem[]>([]);
  readonly projectId = input<string | null>(null);
  readonly persona = input<PersonaEntry | null>(null);
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly tabs = inject(StudioTabStateService);
  readonly criteria = dispatchCriteria;
  readonly projects = computed(() => this.workspaces().flatMap(w => w.projects.map(p => ({ ...p, workspaceId: w.id }))));
  readonly project = signal('');
  readonly workspace = computed(() => this.projects().find(p => p.id === this.project())?.workspaceId ?? '');
  readonly review = signal<DispatchReview | null>(null);
  readonly conflicts = signal<DispatchConflict[]>([]);
  readonly source = signal<'replay' | 'shadow' | 'live'>('replay');
  readonly sourceOptions = [{ value: 'replay', label: 'Replay' }, { value: 'shadow', label: 'Shadow' }, { value: 'live', label: 'Live' }] as const;
  readonly window = signal('30d');
  readonly revision = signal('');
  readonly weights = signal(defaultWeights());
  readonly gate = signal(.8);
  readonly reason = signal('');
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly isOwner = computed(() => this.auth.status()?.user?.role === 'owner');
  readonly remainder = computed(() => 100 - Object.values(this.weights()).reduce((sum, weight) => sum + weight, 0));
  readonly canSave = computed(() => this.isOwner() && !!this.review() && !this.loading() && !this.saving() && validWeights(this.weights(), this.gate()));
  // The server scores the common cohort once. Source selection never normalizes a smaller cohort.
  readonly rows = computed(() => this.review()?.personas.filter(r => r.raw.mode === this.source()
    || r.raw.persona === 'queue-keeper') ?? []);
  readonly selected = computed(() => this.rows().find(r => r.raw.persona === this.persona()?.id) ?? null);
  readonly baseline = computed(() => this.review()?.personas.find(r => r.raw.persona === 'queue-keeper') ?? null);
  readonly attributed = computed(() => this.conflicts().filter(c => c.personaMode === this.source()
    && (!this.persona() || c.personaId === this.persona()?.id)));
  readonly champion = computed(() => this.review()?.competition[0]?.champion ?? null);
  private request = 0;
  private selectedWorkspace = '';
  constructor() {
    effect(() => {
      const projects = this.projects();
      const requested = this.projectId();
      const previous = untracked(this.project);
      const next = requested ? projects.find(p => p.id === requested)?.id ?? ''
        : projects.find(p => p.id === previous)?.id ?? projects[0]?.id ?? '';
      const workspace = projects.find(p => p.id === next)?.workspaceId ?? '';
      if (workspace !== this.selectedWorkspace) {
        this.selectedWorkspace = workspace; this.revision.set(''); this.notice.set('');
      }
      this.project.set(next);
    });
    effect(() => { const project = this.project(); const window = this.window(); const revision = this.revision();
      if (project) this.load(project, window, revision);
      else { ++this.request; this.review.set(null); this.conflicts.set([]); this.loading.set(false); } });
  }
  selectProject(project: string): void {
    if (this.persona()) this.project.set(project);
    else this.tabs.open({ kind: 'team', personaId: dispatchRoute(project) });
  }
  route(project: string): string { return `#/workspace/team/${encodeURIComponent(dispatchRoute(project))}`; }
  label(id: string): string { const title = id.replace(/-/g, ' '); return title.charAt(0).toUpperCase() + title.slice(1); }
  format(value: number | null | undefined): string { return value == null || !Number.isFinite(value) ? 'Unmeasured' : value.toLocaleString('en-US', { maximumFractionDigits: 2 }); }
  syncCount(row: DispatchRow | null): string {
    return row?.raw.criteria.conflicts == null ? 'Unmeasured' : this.format(Math.round(row.raw.criteria.conflicts * row.raw.integratedCards / 100));
  }
  score(row: DispatchRow, key: Criterion): string { return row.scores[key] == null ? (row.raw.mode === 'replay' ? 'Not in replay' : 'Unmeasured') : this.format(row.scores[key]); }
  load(project = this.project(), window = this.window(), revision = this.revision()): void {
    const request = ++this.request;
    this.loading.set(true); this.error.set(''); this.review.set(null); this.conflicts.set([]);
    const query = new URLSearchParams({ window }); if (revision) query.set('weights', revision);
    const name = this.projects().find(p => p.id === project)?.displayName ?? project;
    const root = `/api/projects/${encodeURIComponent(name)}/dispatch`;
    forkJoin({ review: this.http.get<DispatchReview>(`${root}/review?${query}`),
      conflicts: this.http.get<DispatchConflict[]>(`${root}/conflicts?${this.conflictRange(window)}`) }).subscribe({
      next: data => { if (request !== this.request) return;
        this.review.set(data.review);
        const mode = data.review.personas.find(r => r.raw.persona === this.persona()?.id)?.raw.mode;
        if (mode === 'replay' || mode === 'shadow' || mode === 'live') this.source.set(mode);
        this.conflicts.set(data.conflicts.filter(c => Date.parse(c.at) >= Date.parse(data.review.from) && Date.parse(c.at) < Date.parse(data.review.to)));
        this.weights.set({ ...data.review.weights.weights }); this.gate.set(data.review.weights.gate); this.loading.set(false); },
      error: () => { if (request !== this.request) return;
        this.loading.set(false); this.error.set('Dispatch review could not be loaded. Retry or choose another project or window.'); },
    });
  }
  private conflictRange(window: string): string {
    const [from, to] = window.split('/');
    if (to) return new URLSearchParams({ from, to }).toString();
    const end = new Date(); end.setUTCHours(0, 0, 0, 0); end.setUTCDate(end.getUTCDate() + 1);
    const start = new Date(end); start.setUTCDate(start.getUTCDate() - 30);
    return new URLSearchParams({ from: start.toISOString(), to: end.toISOString() }).toString();
  }
  editWeight(key: Criterion, value: number): void { this.weights.update(w => ({ ...w, [key]: value })); this.notice.set(''); }
  reset(): void { this.weights.set(defaultWeights()); this.gate.set(.8); this.notice.set('Defaults restored in the editor. Save to create a revision.'); }
  save(): void {
    if (!this.canSave()) return;
    const project = this.project(); const workspace = this.workspace();
    this.saving.set(true); this.error.set(''); this.notice.set('');
    this.http.put<WeightRevision>(`/api/workspaces/${encodeURIComponent(workspace)}/dispatch/review/weights`, {
      weights: this.weights(), gate: this.gate(), reason: this.reason().trim() || null,
    }).subscribe({ next: revision => {
      this.saving.set(false); this.reason.set(''); this.notice.set(`Saved ${revision.id}. Rankings use the new revision.`);
      if (this.project() === project && this.workspace() === workspace) {
        if (this.revision() === revision.id) this.load(); else this.revision.set(revision.id);
      }
    }, error: (error: HttpErrorResponse) => { this.saving.set(false);
      this.error.set(error.status === 403 ? 'Only a human Owner can save review weights.' : 'Weights could not be saved. Your edits are preserved; retry Save as new revision.'); },
    });
  }
}

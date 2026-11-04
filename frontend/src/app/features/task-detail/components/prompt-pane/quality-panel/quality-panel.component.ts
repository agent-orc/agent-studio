import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { QualityChangeRound, QualityChangeView, ReviewEvidenceEntry, TaskInfo } from '../../../../../models/task.model';
import { QualityStudioService } from '../../../../../services/quality-studio.service';

const STATE_LABELS: Record<QualityChangeView['state'], string> = {
  'not-registered': 'Repository not registered', waiting: 'Waiting for review',
  reviewing: 'Reviewing', pass: 'Pass', concerns: 'Concerns', block: 'Block',
  unavailable: 'Unavailable', shadow: 'Shadow', outdated: 'Outdated',
  'not-reviewed': 'Not reviewed',
};

@Component({
  selector: 'app-quality-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './quality-panel.component.html',
  styleUrl: './quality-panel.component.scss',
})
export class QualityPanelComponent {
  private readonly http = inject(HttpClient);
  private readonly qualityStudio = inject(QualityStudioService);
  readonly job = input.required<TaskInfo>();
  readonly evidence = input<ReviewEvidenceEntry[]>([]);
  readonly view = signal<QualityChangeView | null>(null);
  readonly selectedAttempt = signal<string | null>(null);
  readonly qualityRows = computed(() => {
    const fingerprints = new Set(this.selected()?.document?.findings.map(finding => finding.fingerprint) ?? []);
    return this.evidence().filter(row => row.id.startsWith('quality-studio:')
      && !fingerprints.has(row.id.slice('quality-studio:'.length)));
  });
  readonly rounds = computed(() => this.view()?.rounds ?? []);
  readonly selected = computed(() => this.rounds().find(round => round.attemptId === this.selectedAttempt())
    ?? this.rounds().at(-1) ?? null);
  readonly selectedState = computed<QualityChangeView['state']>(() => {
    const view = this.view();
    const round = this.selected();
    if (!view || !round || this.isLatest(round)) return view?.state ?? 'unavailable';
    if (round.mode === 'off') return 'not-reviewed';
    if (round.unavailableReason) return 'unavailable';
    const verdict = round.document?.verdict;
    if (!verdict) return 'not-reviewed';
    if (verdict !== 'pass' && verdict !== 'concerns' && verdict !== 'block') return 'unavailable';
    return round.mode === 'shadow' ? 'shadow' : verdict;
  });
  readonly previous = computed(() => {
    const rounds = this.rounds();
    const index = rounds.findIndex(round => round.attemptId === this.selected()?.attemptId);
    return index > 0 ? rounds[index - 1] : null;
  });
  readonly newCount = computed(() => {
    const current = this.selected()?.document?.findings ?? [];
    const previous = new Set(this.previous()?.document?.findings.map(finding => finding.fingerprint) ?? []);
    return current.filter(finding => !previous.has(finding.fingerprint)).length;
  });
  readonly counts = computed(() => {
    const findings = this.selected()?.document?.findings ?? [];
    return ['critical', 'high', 'medium', 'low', 'info'].map(severity => ({
      severity, count: findings.filter(finding => finding.severity === severity).length,
    }));
  });

  private readonly load = effect(onCleanup => {
    const job = this.job();
    const params = new HttpParams().set('watchPath', job.watchPath);
    this.selectedAttempt.set(null);
    this.view.set(null);
    const subscription = this.http.get<QualityChangeView>(
      '/api/tasks/' + encodeURIComponent(job.id) + '/quality-studio', { params }).subscribe({
      next: view => this.view.set(view),
      error: () => this.view.set({ state: 'unavailable', repositoryId: null, currentHead: null,
        rounds: [], landedSha: null, landedBaseSha: null, landedPatchEqualsReviewedPatch: null }),
    });
    onCleanup(() => subscription.unsubscribe());
  });

  label(state: QualityChangeView['state']): string { return STATE_LABELS[state]; }
  short(sha: string): string { return sha.slice(0, 7); }
  isLatest(round: QualityChangeRound): boolean { return this.rounds().at(-1)?.attemptId === round.attemptId; }
  select(attemptId: string): void { this.selectedAttempt.set(attemptId); }

  open(round: QualityChangeRound): void {
    const job = this.job();
    if (!round.repositoryId) return;
    this.qualityStudio.openRange(job.projectName, round.repositoryId, round.baseSha, round.headSha, job.taskKey);
  }

  openLanded(): void {
    const view = this.view();
    const job = this.job();
    if (!view?.repositoryId || !view.landedBaseSha || !view.landedSha) return;
    this.qualityStudio.openRange(job.projectName, view.repositoryId,
      view.landedBaseSha, view.landedSha, job.taskKey);
  }
}

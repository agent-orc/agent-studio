import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { BatchGateAdvisorEffective, BatchGateSettings } from '../../../models/batch-gate.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { BatchPhaseMetricsComponent } from '../batch-phase-metrics/batch-phase-metrics';

@Component({
  selector: 'app-batch-settings',
  standalone: true,
  imports: [FormsModule, BatchPhaseMetricsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-settings.html',
  styleUrl: './batch-settings.scss',
})
export class BatchSettingsComponent {
  readonly project = input.required<string>();
  private readonly gate = inject(BatchGateService);
  readonly state = computed(() => this.gate.state(this.project())());
  readonly settings = signal<BatchGateSettings | null>(null);
  readonly advisorEffective = signal<BatchGateAdvisorEffective | null>(null);
  readonly tagsDraft = signal('');
  readonly maximum = signal(8);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly fieldError = signal<string | null>(null);
  readonly saved = signal(false);
  /** Set when enabling switched a documentation scope to code, so the form says so. */
  readonly scopeDefaulted = signal(false);

  constructor() {
    effect(() => this.load(this.project()));
    effect(onCleanup => onCleanup(this.gate.watch(this.project())));
  }
  load(project: string): void {
    if (!project) return;
    this.loading.set(true);
    this.error.set(null);
    this.gate.getSettings(project).subscribe({
      next: data => {
        this.settings.set({ ...data.settings, urgentTags: [...data.settings.urgentTags], advisor: { ...data.settings.advisor } });
        this.tagsDraft.set(data.settings.urgentTags.join(', '));
        this.maximum.set(data.limits.hardMaximumSize);
        this.advisorEffective.set(data.advisorEffective ?? null);
        this.scopeDefaulted.set(false);
        this.loading.set(false);
      },
      error: () => { this.error.set('Could not load batch settings.'); this.loading.set(false); },
    });
  }
  update<K extends keyof BatchGateSettings>(key: K, value: BatchGateSettings[K]): void {
    this.settings.update(current => current ? { ...current, [key]: value } : current);
    this.fieldError.set(null);
    this.saved.set(false);
  }
  /**
   * Enabling the gate on the documentation scope would leave code cards on the
   * per-task gate, so enabling defaults the scope to code and says so (AGT-3592).
   */
  setEnabled(enabled: boolean): void {
    const switchScope = enabled && this.settings()?.enabled === false && this.settings()?.scope === 'documentation';
    this.update('enabled', enabled);
    if (switchScope) this.update('scope', 'code');
    this.scopeDefaulted.set(switchScope);
  }
  setScope(scope: BatchGateSettings['scope']): void {
    this.update('scope', scope);
    this.scopeDefaulted.set(false);
  }
  advisor(key: keyof BatchGateSettings['advisor'], value: string | number): void {
    this.settings.update(current => current ? { ...current, advisor: { ...current.advisor, [key]: value } } : current);
    this.fieldError.set(null);
    this.saved.set(false);
  }
  parseTags(value: string): string[] { return value.split(',').map(tag => tag.trim()).filter(Boolean); }
  save(): void {
    const settings = this.settings();
    if (!settings || this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    this.fieldError.set(null);
    const model = settings.advisor.model?.trim();
    const request: BatchGateSettings = {
      ...settings,
      urgentTags: this.parseTags(this.tagsDraft()),
      advisor: { ...settings.advisor, ...(model ? { model } : { model: undefined }) },
    };
    if (!model) delete request.advisor.model;
    this.gate.putSettings(this.project(), request).subscribe({
      next: data => { this.settings.set(data.settings); this.tagsDraft.set(data.settings.urgentTags.join(', ')); this.maximum.set(data.limits.hardMaximumSize); this.advisorEffective.set(data.advisorEffective ?? null); this.saved.set(true); this.saving.set(false); },
      error: err => {
        this.error.set(err?.error?.error ?? 'Could not save batch settings.');
        this.fieldError.set(err?.error?.field ?? null);
        this.saving.set(false);
      },
    });
  }
  invalid(field: string): boolean { return this.fieldError() === field || this.fieldError() === `advisor.${field}`; }
}

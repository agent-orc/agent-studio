import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { BatchGateSettings } from '../../../models/batch-gate.model';
import { BatchGateService } from '../../../services/batch-gate.service';

@Component({
  selector: 'app-batch-settings',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-settings.html',
  styleUrl: './batch-settings.scss',
})
export class BatchSettingsComponent {
  readonly project = input.required<string>();
  private readonly gate = inject(BatchGateService);
  readonly settings = signal<BatchGateSettings | null>(null);
  readonly maximum = signal(8);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly fieldError = signal<string | null>(null);
  readonly saved = signal(false);

  constructor() {
    effect(() => this.load(this.project()));
  }
  load(project: string): void {
    if (!project) return;
    this.loading.set(true);
    this.error.set(null);
    this.gate.getSettings(project).subscribe({
      next: data => {
        this.settings.set({ ...data.settings, urgentTags: [...data.settings.urgentTags], advisor: { ...data.settings.advisor } });
        this.maximum.set(data.limits.hardMaximumSize);
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
    this.gate.putSettings(this.project(), settings).subscribe({
      next: data => { this.settings.set(data.settings); this.maximum.set(data.limits.hardMaximumSize); this.saved.set(true); this.saving.set(false); },
      error: err => {
        this.error.set(err?.error?.error ?? 'Could not save batch settings.');
        this.fieldError.set(err?.error?.field ?? null);
        this.saving.set(false);
      },
    });
  }
  invalid(field: string): boolean { return this.fieldError() === field || this.fieldError() === `advisor.${field}`; }
}

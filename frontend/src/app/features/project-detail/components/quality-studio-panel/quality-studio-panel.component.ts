import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { QualityStudioService, type QualityStudioState } from '../../../../services/quality-studio.service';
import { AuthSessionState } from '../../../../services/auth.service';
import { QualityStudioStatusService } from './quality-studio-status.service';

@Component({
  selector: 'app-quality-studio-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './quality-studio-panel.component.html',
  styleUrl: './quality-studio-panel.component.scss',
})
export class QualityStudioPanelComponent {
  readonly projectName = input.required<string>();
  readonly openSettings = output<void>();
  readonly quality = inject(QualityStudioService);
  readonly registration = inject(QualityStudioStatusService, { optional: true });
  private readonly auth = inject(AuthSessionState);
  readonly owner = computed(() => this.auth.status()?.user?.role === 'owner');
  readonly status = computed(() => this.quality.status(this.projectName()));
  readonly repositoryDraft = signal('');
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    effect(() => this.quality.ensure(this.projectName()));
    effect(() => this.repositoryDraft.set(this.status()?.repositoryId ?? ''));
  }

  saveRepository(): void {
    this.saving.set(true);
    this.error.set(null);
    this.quality.setRepository(this.projectName(), this.repositoryDraft().trim(), error => {
      this.saving.set(false);
      this.error.set(error ?? null);
    });
  }

  open(): void { this.quality.open(this.projectName()); }

  stateLabel(state: QualityStudioState): string {
    return {
      'not-connected': 'Not connected',
      unreachable: 'Unreachable',
      'version-too-old': 'Version too old',
      'not-registered': 'Not registered',
      ready: 'Connected',
    }[state];
  }
}

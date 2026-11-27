import { ChangeDetectionStrategy, Component, effect, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DialogComponent } from '@studio/ui/components/dialog/dialog.component';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/index';
import type { ProjectUrlStartRule, RegistryProjectUrl } from '../../../../models/task.model';

export interface ProjectUrlSettingsValue {
  label: string;
  url: string;
  startRule: ProjectUrlStartRule | null;
}

/** Compact editor for the URL and start rule of the currently open embed. */
@Component({
  selector: 'app-project-url-settings-dialog',
  standalone: true,
  imports: [DialogComponent, FormsModule, PendingButtonDirective],
  templateUrl: './project-url-settings-dialog.html',
  styleUrl: './project-url-settings-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectUrlSettingsDialogComponent {
  readonly url = input.required<RegistryProjectUrl>();
  readonly saving = input(false);
  readonly error = input<string | null>(null);
  readonly saveRequest = output<ProjectUrlSettingsValue>();
  readonly closeRequest = output<void>();

  readonly address = signal('');
  readonly command = signal('');
  readonly cwd = signal('');
  readonly port = signal<number | null>(null);
  readonly healthUrl = signal('');
  readonly silenceTimeout = signal(30);
  readonly startupTimeout = signal(600);

  constructor() {
    effect(() => {
      const url = this.url();
      this.address.set(url.url);
      this.command.set(url.startRule?.command ?? '');
      this.cwd.set(url.startRule?.cwd ?? '');
      this.port.set(url.startRule?.port ?? null);
      this.healthUrl.set(url.startRule?.healthUrl ?? url.url);
      this.silenceTimeout.set(url.startRule?.readinessTimeoutSeconds ?? 30);
      this.startupTimeout.set(url.startRule?.startupTimeoutSeconds ?? 600);
    });
  }

  save(): void {
    const current = this.url();
    const command = this.command().trim();
    this.saveRequest.emit({
      label: current.label,
      url: this.address().trim(),
      startRule: command ? {
        command,
        cwd: this.cwd().trim() || null,
        port: this.port(),
        healthUrl: this.healthUrl().trim() || null,
        readinessTimeoutSeconds: Math.max(2, Math.min(300, this.silenceTimeout() || 30)),
        startupTimeoutSeconds: Math.max(2, Math.min(3600, this.startupTimeout() || 600)),
        source: 'manual',
      } : null,
    });
  }
}

import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { AuthSessionState } from '../../../../services/auth.service';

interface IncidentWebhookView { configured: boolean; destination: string; }

@Component({
  selector: 'app-incident-webhook-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './incident-webhook-settings.component.html',
  styleUrl: './incident-webhook-settings.component.scss',
})
export class IncidentWebhookSettingsComponent {
  readonly projectName = input.required<string>();
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthSessionState);
  readonly owner = computed(() => this.auth.status()?.profile === 'local' || this.auth.status()?.user?.role === 'owner');
  readonly url = signal('');
  readonly destination = signal('');
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  private loadedProject = '';

  constructor() {
    effect(() => {
      if (!this.owner()) return;
      const project = this.projectName();
      if (project === this.loadedProject) return;
      this.loadedProject = project;
      this.http.get<IncidentWebhookView>(this.path()).subscribe({
        next: value => this.destination.set(value.destination),
        error: () => this.message.set('Could not load incident notifications.'),
      });
    });
  }

  private path(): string {
    return `/api/projects/${encodeURIComponent(this.projectName())}/incident-webhook`;
  }

  save(): void {
    if (!this.url().trim() || this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    this.http.put<IncidentWebhookView>(this.path(), {
      url: this.url().trim(), studioBaseUrl: window.location.origin,
    }).subscribe({
      next: value => {
        this.destination.set(value.destination);
        this.url.set('');
        this.busy.set(false);
        this.message.set('Webhook saved. Send a test message to check delivery.');
      },
      error: () => { this.busy.set(false); this.message.set('Could not save the webhook. Use an HTTPS URL without user info.'); },
    });
  }

  test(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    this.http.post(this.path() + '/test', {}).subscribe({
      next: () => { this.busy.set(false); this.message.set('Test message delivered.'); },
      error: () => { this.busy.set(false); this.message.set('Test delivery failed. Check the receiver and try again.'); },
    });
  }

  remove(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    this.http.put<IncidentWebhookView>(this.path(), { url: null, studioBaseUrl: null }).subscribe({
      next: () => { this.destination.set(''); this.busy.set(false); this.message.set('Webhook removed.'); },
      error: () => { this.busy.set(false); this.message.set('Could not remove the webhook.'); },
    });
  }
}

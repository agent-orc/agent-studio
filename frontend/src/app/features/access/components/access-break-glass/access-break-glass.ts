import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../../../services/auth.service';
import { getPasskey } from '../../../../services/webauthn';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { formatAccessTime } from '../../models/access.model';

interface BreakGlassWindow {
  id: string;
  sessionIds: string[];
  project: string;
  openedAt: string;
  expiresAt: string;
  closedAt: string | null;
  reviewCardId: string | null;
}

const URL = '/api/auth/iam/break-glass';

@Component({
  selector: 'app-access-break-glass',
  standalone: true,
  imports: [PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-break-glass.html',
  styleUrl: './access-break-glass.scss',
})
export class AccessBreakGlassComponent implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  readonly windows = signal<readonly BreakGlassWindow[]>([]);
  readonly credential = signal('');
  readonly issued = signal('');
  readonly code = signal('');
  readonly sessions = signal('');
  readonly project = signal('');
  readonly hours = signal(2);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly time = formatAccessTime;

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    try { this.windows.set(await firstValueFrom(this.http.get<BreakGlassWindow[]>(`${URL}/windows`))); }
    catch { this.error.set('Break-glass windows could not be loaded.'); }
  }

  async issue(): Promise<void> {
    await this.mutate(async () => {
      await this.stepUp();
      const result = await firstValueFrom(this.http.post<{ credential: string }>(`${URL}/credential`, {}));
      this.issued.set(result.credential);
    });
  }

  async open(): Promise<void> {
    await this.mutate(async () => {
      await this.stepUp();
      await firstValueFrom(this.http.post(`${URL}/windows`, {
        credential: this.credential().trim(),
        sessionIds: this.sessions().split(',').map(value => value.trim()).filter(Boolean),
        project: this.project().trim(),
        lifetimeHours: this.hours(),
      }));
      this.credential.set('');
      this.issued.set('');
      await this.load();
    });
  }

  async close(id: string): Promise<void> {
    await this.mutate(async () => {
      await firstValueFrom(this.http.post(`${URL}/windows/${encodeURIComponent(id)}/close`, {}));
      await this.load();
    });
  }

  private async stepUp(): Promise<void> {
    const code = this.code().trim();
    if (code) await firstValueFrom(this.auth.stepUp(code));
    else {
      const options = await firstValueFrom(this.auth.passkeyStepUpOptions());
      await firstValueFrom(this.auth.stepUp(undefined, await getPasskey(options)));
    }
    this.code.set('');
  }

  private async mutate(action: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try { await action(); }
    catch (failure) {
      const body = failure instanceof HttpErrorResponse ? failure.error as { message?: string } : null;
      this.error.set(body?.message || 'Break-glass action failed.');
    } finally { this.busy.set(false); }
  }
}

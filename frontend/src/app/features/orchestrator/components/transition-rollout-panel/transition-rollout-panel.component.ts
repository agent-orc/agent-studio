import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

interface DecisionReceipt {
  occurrence: string;
  inputsDigest: string;
  action: string;
  reason: string;
}

interface Mismatch {
  monolith: DecisionReceipt;
  engine: DecisionReceipt;
}

interface Profile {
  profile: string;
  state: 'monolith' | 'shadow' | 'ready' | 'engine' | 'rolled-back';
  streak: number;
  threshold: number;
  mismatchCount: number;
  lastMismatch: Mismatch | null;
}

@Component({
  selector: 'app-transition-rollout-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transition-rollout-panel.component.html',
  styleUrl: './transition-rollout-panel.component.scss',
})
export class TransitionRolloutPanelComponent implements OnInit {
  private readonly http = inject(HttpClient);
  readonly profiles = signal<readonly Profile[]>([]);
  readonly reason = signal('');
  readonly busy = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  ngOnInit(): void { void this.refresh(); }

  async refresh(): Promise<void> {
    try {
      this.profiles.set(await firstValueFrom(
        this.http.get<Profile[]>('/api/orchestration/rollout/profiles')));
      this.error.set(null);
    } catch {
      this.error.set('Could not load transition profiles.');
    }
  }

  async switch(profile: Profile, target: 'shadow' | 'engine' | 'rollback'): Promise<void> {
    const reason = this.reason().trim();
    if (!reason || this.busy()) return;
    this.busy.set(profile.profile);
    try {
      await firstValueFrom(this.http.post(
        `/api/orchestration/rollout/profiles/${profile.profile}/switch`, { target, reason }));
      this.reason.set('');
      await this.refresh();
    } catch {
      this.error.set(`Could not switch ${profile.profile}. Check its current state and try again.`);
    } finally {
      this.busy.set(null);
    }
  }
}

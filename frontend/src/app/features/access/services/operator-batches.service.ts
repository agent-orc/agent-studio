import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../../services/auth.service';
import { getPasskey } from '../../../services/webauthn';
import type { OperatorBatch } from '../models/operator-batch.model';

const PATH = '/api/auth/iam/operator-batches';
@Injectable({ providedIn: 'root' })
export class OperatorBatchesService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  readonly batches = signal<OperatorBatch[]>([]);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  async load(): Promise<void> {
    try {
      this.batches.set(await firstValueFrom(this.http.get<OperatorBatch[]>(PATH)));
    } catch {
      this.error.set('Operator batches could not be loaded.');
    }
  }

  async decide(id: string, selected: number[], approve: boolean, code: string): Promise<boolean> {
    this.busy.set(true);
    this.error.set(null);
    try {
      if (code.trim()) await firstValueFrom(this.auth.stepUp(code.trim()));
      else {
        const options = await firstValueFrom(this.auth.passkeyStepUpOptions());
        await firstValueFrom(this.auth.stepUp(undefined, await getPasskey(options)));
      }
      await firstValueFrom(this.http.post<OperatorBatch>(`${PATH}/${encodeURIComponent(id)}/decide`, { selected, approve }));
      await this.load();
      return true;
    } catch {
      this.error.set('The batch was not confirmed. Check your second factor and refresh the batch.');
      await this.load();
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}

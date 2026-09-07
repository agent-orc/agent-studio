import { Injectable, inject, signal } from '@angular/core';
import type { CodexSignInTarget } from '../models/provider-auth.model';
import { RemoteHostsService } from './remote-hosts.service';

@Injectable({ providedIn: 'root' })
export class CodexSignInDialogService {
  private readonly hosts = inject(RemoteHostsService);
  readonly request = signal<CodexSignInTarget | null>(null);

  open(target: CodexSignInTarget): void {
    this.hosts.ensureLoaded();
    const aliases = new Set(target.aliases.map(alias => alias.toLowerCase()));
    const host = this.hosts.hosts().find(candidate =>
      aliases.has(candidate.id.toLowerCase())
      || aliases.has(candidate.clientId.toLowerCase())
      || aliases.has(candidate.name.toLowerCase()));
    const configuredTarget = target.sshTarget ?? host?.address ?? null;
    this.request.set({
      ...target,
      sshTarget: configuredTarget?.trim().replace(/^ssh:\/\//i, '') || target.hostName,
    });
  }

  close(): void {
    this.request.set(null);
  }

  refreshHosts(): void {
    this.hosts.refresh();
  }
}

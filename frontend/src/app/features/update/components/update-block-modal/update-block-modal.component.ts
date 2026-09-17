import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';

import { UpdateClientService } from '../../../../services/update.service';
import { UpdatePhase } from '../../../../models/update-service.model';
import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';

/**
 * Full-screen, click-blocking modal that takes over the UI while an update
 * is in flight. Stays alive across an F5 reload because the FE keeps
 * polling the standalone UpdateService on :5039 — even when the main
 * backend is mid-restart, isRunning stays true, the modal stays mounted.
 *
 * ADR-0031: the modal stays mounted across the full 9-phase pipeline
 * (`phase ∈ {preparing, pausing-runners, pulling, building, restarting,
 * verifying-after-restart, resuming, rolling-back}`). Each phase emits a
 * server-side `phaseLabel` (gerund) which we render verbatim; a fallback
 * map covers older Update Service builds that don't ship the label yet.
 *
 * No "cancel" button: an in-flight stable restart must run to completion
 * (see UpdateOrchestrator.RunOrchestrationAsync).
 */
@Component({
  selector: 'app-update-block-modal',
  standalone: true,
  imports: [OverlayPortalDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './update-block-modal.component.html',
  styleUrl: './update-block-modal.component.scss',
})
export class UpdateBlockModalComponent {
  private readonly client = inject(UpdateClientService);

  readonly isRunning = this.client.isRunning;
  readonly status = this.client.status;

  readonly backendReachable = computed(() => this.status()?.backendReachable ?? false);

  readonly titleLine = computed(() => {
    const s = this.status();
    if (s?.phase === 'rolling-back') return 'Rolling back update';
    return 'Updating Stable';
  });

  readonly phaseLine = computed(() => {
    const s = this.status();
    if (!s) return 'Starting…';
    // Server-supplied label takes precedence (ADR-0031); fall back to a
    // local map so older Update Service builds still render readable text.
    const label = s.phaseLabel ?? humanPhase(s.phase);
    return s.message ? `${label} — ${s.message}` : label;
  });
}

function humanPhase(phase: UpdatePhase | string): string {
  switch (phase) {
    case 'preparing':
      return 'Preparing snapshot';
    case 'pausing-runners':
      return 'Pausing runners';
    case 'pulling':
      return 'Pulling and rebuilding';
    case 'building':
      return 'Building';
    case 'restarting':
      return 'Restarting backend';
    case 'verifying-after-restart':
      return 'Verifying restart';
    case 'resuming':
      return 'Resuming runners';
    case 'rolling-back':
      return 'Rolling back';
    case 'done':
      return 'Update verified';
    case 'degraded':
      return 'Backend up, frontend down';
    case 'failed':
      return 'Update failed';
    default:
      return String(phase);
  }
}

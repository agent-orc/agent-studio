import type { ConfirmDialogOptions } from '@studio/ui/models/app-dialog.model';
import type { StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';
import { formatAge, type EnvironmentStatus, type Release } from './deploy-area.model';

/** Runtime health of a release while it ran, from the backend ledger (F13). */
export type RuntimeHealth = 'healthy' | 'incident' | 'unknown';

/** `GET /deploy/status` `rollbackTarget`: the release a rollback of the environment runs now. */
export interface RollbackTarget {
  release: Release;
  deployedAtUtc: string | null;
  runtimeHealth: string;
  incident: { source: string; startedAtUtc: string; detail: string } | null;
  /** Older releases passed over because they had an incident while running. */
  skippedVersions: string[];
}

/** What every rollback entry point shows before the owner confirms. */
export interface RollbackOffer {
  environmentId: string;
  environmentName: string;
  production: boolean;
  /** Null while `releaseUncertain`: the lifecycle baseline is then not named as the running release. */
  currentVersion: string | null;
  currentReleaseId: string | null;
  currentUncertain: boolean;
  targetVersion: string;
  targetCreatedAt: string;
  targetDeployedAt: string | null;
  health: RuntimeHealth;
  incidentDetail: string | null;
  skippedVersions: string[];
}

const RUNTIME_HEALTH: Readonly<Record<RuntimeHealth, { tone: StatusTone; label: string }>> = {
  healthy: { tone: 'success', label: 'Ran without incident' },
  // A past incident of an older release is a caution, not an acute alarm (R4).
  incident: { tone: 'warning', label: 'Had an incident while running' },
  unknown: { tone: 'neutral', label: 'No runtime health recorded' },
};

export function runtimeHealth(health: RuntimeHealth): { tone: StatusTone; label: string } {
  return RUNTIME_HEALTH[health];
}

function healthOf(value: string): RuntimeHealth {
  return value === 'healthy' || value === 'incident' ? value : 'unknown';
}

/** One offer per environment status that names a rollback target; previews have none. */
export function buildRollbackOffers(statuses: readonly EnvironmentStatus[] | null): RollbackOffer[] {
  return (statuses ?? []).flatMap(status => {
    const target = status.rollbackTarget;
    if (!target || status.environment.kind === 'preview') return [];
    const uncertain = status.releaseUncertain === true;
    return [{
      environmentId: status.environment.id,
      environmentName: status.environment.name,
      production: status.environment.kind === 'production',
      currentVersion: uncertain ? null : status.currentRelease?.version ?? null,
      currentReleaseId: uncertain ? null : status.currentRelease?.id ?? null,
      currentUncertain: uncertain,
      targetVersion: target.release.version,
      targetCreatedAt: target.release.createdAtUtc,
      targetDeployedAt: target.deployedAtUtc,
      health: healthOf(target.runtimeHealth),
      incidentDetail: target.incident?.detail ?? null,
      skippedVersions: target.skippedVersions ?? [],
    }];
  });
}

function ago(iso: string | null, now: number): string | null {
  const age = formatAge(iso, now);
  return age && age !== 'just now' ? `${age} ago` : age;
}

/**
 * The confirmation names the current and the target release, the target's
 * age and runtime health. A target with an incident must be typed out, so it
 * is never chosen in passing.
 */
export function rollbackConfirmation(offer: RollbackOffer, now: number): ConfirmDialogOptions {
  const from = offer.currentVersion ? `Release ${offer.currentVersion}`
    : offer.currentUncertain ? 'an uncertain running release' : 'the current release';
  const health = runtimeHealth(offer.health).label.toLowerCase();
  const items = [
    offer.currentUncertain ? 'Current: running release uncertain. A failed release may be running.' : `Current: ${from}`,
    `Target: Release ${offer.targetVersion}, created ${ago(offer.targetCreatedAt, now) ?? 'at an unknown time'}`
      + (offer.targetDeployedAt ? `, last deployed here ${ago(offer.targetDeployedAt, now)}` : ''),
    `Runtime health of the target: ${health}${offer.incidentDetail ? ` (${offer.incidentDetail})` : ''}`,
  ];
  if (offer.skippedVersions.length)
    items.push(`Skipped: Release ${offer.skippedVersions.join(', Release ')} had an incident while running`);
  return {
    eyebrow: offer.production ? 'Production rollback' : 'Rollback',
    title: `Roll back ${offer.environmentName} to Release ${offer.targetVersion}?`,
    message: offer.production
      ? `${offer.environmentName} leaves ${from} and runs Release ${offer.targetVersion}. Confirming approves the rollback as Owner or Maintainer.`
      : `${offer.environmentName} leaves ${from} and runs Release ${offer.targetVersion}.`,
    items,
    confirmLabel: `Roll back ${offer.environmentName}`,
    cancelLabel: offer.currentUncertain ? 'Keep the current state' : `Keep ${from}`,
    kind: 'danger',
    ...(offer.health === 'incident' ? {
      requireTypedValues: [offer.targetVersion],
      requireTypedPrompt: `Release ${offer.targetVersion} had an incident while running. Type its version to roll back to it anyway.`,
    } : {}),
  };
}

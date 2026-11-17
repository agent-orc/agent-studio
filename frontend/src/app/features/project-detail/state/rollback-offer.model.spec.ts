import { DEPLOY_FIXTURE_NOW, RELEASE_1, RELEASE_2, RELEASE_3, deployStatusFixture, productionRollbackFixture } from '../testing/deploy-fixtures';
import { buildRollbackOffers, rollbackConfirmation, runtimeHealth } from './rollback-offer.model';

describe('rollback offers (F13)', () => {
  it('offers nothing without a rollback target, as an older server answers', () => {
    expect(buildRollbackOffers(deployStatusFixture())).toEqual([]);
    expect(buildRollbackOffers(null)).toEqual([]);
  });

  it('names current and target release, the target age, runtime health and the skipped release', () => {
    const [offer] = buildRollbackOffers(productionRollbackFixture().statuses);
    expect(offer).toEqual(expect.objectContaining({
      environmentId: 'production', production: true, currentVersion: RELEASE_3.version, currentReleaseId: RELEASE_3.id,
      targetVersion: RELEASE_1.version, targetCreatedAt: RELEASE_1.createdAtUtc, health: 'healthy',
      skippedVersions: [RELEASE_2.version],
    }));

    const dialog = rollbackConfirmation(offer, DEPLOY_FIXTURE_NOW);
    expect(dialog.title).toBe(`Roll back Production to Release ${RELEASE_1.version}?`);
    expect(dialog.message).toContain(`Production leaves Release ${RELEASE_3.version} and runs Release ${RELEASE_1.version}`);
    expect(dialog.message).toContain('approves the rollback as Owner or Maintainer');
    expect(dialog.items).toEqual([
      `Current: Release ${RELEASE_3.version}`,
      `Target: Release ${RELEASE_1.version}, created 1 d ago, last deployed here 13 h ago`,
      'Runtime health of the target: ran without incident',
      `Skipped: Release ${RELEASE_2.version} had an incident while running`,
    ]);
    expect(dialog.kind).toBe('danger');
    expect(dialog.requireTypedValues).toBeUndefined();
  });

  it('makes a target with an incident a typed choice, never a silent one', () => {
    const [offer] = buildRollbackOffers(productionRollbackFixture(true).statuses);
    expect(offer.health).toBe('incident');
    expect(runtimeHealth(offer.health)).toEqual({ tone: 'warning', label: 'Had an incident while running' });

    const dialog = rollbackConfirmation(offer, DEPLOY_FIXTURE_NOW);
    expect(dialog.items).toContain('Runtime health of the target: had an incident while running (the host reported crash-loop)');
    expect(dialog.requireTypedValues).toEqual([RELEASE_2.version]);
    expect(dialog.requireTypedPrompt).toContain(`Release ${RELEASE_2.version} had an incident while running`);
  });
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { plan } from './migrate-decision-cards.mjs';

const prose = `# Stable release contract

## Decision needed (operator)

Option A, lock file: re-enable RestorePackagesWithLockFile, commit backend/packages.lock.json, restore with --locked-mode. Cost: every dependency change must update the lock file.

Option B, no lock file: derive identity from PackageReference plus restored .nupkg.metadata, update the generator, validator and update service.

## Requirements (after the decision)

1. Implement the chosen option so the manifest generator succeeds on a clean checkout.
2. Document the first tagged release.

## Out of scope

Approving the tag.
`;

function card(key, state, promptMarkdown = prose) {
  return { info: { id: key.toLowerCase(), key, kind: 'task', state }, promptMarkdown };
}

test('AGT-2792 keeps both consequences and common requirements in the choice', () => {
  const migration = plan(card('AGT-2792', '1-preparation'));
  assert.equal(migration.decision.options.length, 2);
  assert.deepEqual(migration.decision.options.map(option => option.id), ['A', 'B']);
  assert.match(migration.decision.options[0].consequences, /every dependency change/);
  assert.match(migration.decision.options[1].consequences, /nupkg.metadata/);
  assert.equal(migration.decision.recommendedOptionId, 'A');
  assert.match(migration.decision.options[0].requirements[0].promptMarkdown, /first tagged release/);
  assert.match(migration.decision.options[0].requirements[0].promptMarkdown, /Source decision: AGT-2792/);
});

test('settled and unrecognisable cards are never planned for mutation', () => {
  assert.equal(plan(card('AGT-2792', '7-archive')).decision, undefined);
  assert.equal(plan(card('AGT-2736', '6-completed')).decision, undefined);
  assert.equal(plan(card('AGT-2792', '1-preparation', '# Ordinary implementation')).decision, undefined);
  assert.equal(plan(card('AGT-2792', '1-preparation', prose + '\nOperator decision recorded: option A')).decision, undefined);
});

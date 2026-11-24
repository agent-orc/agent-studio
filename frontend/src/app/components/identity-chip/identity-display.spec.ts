import { generatedHostAlias, hostDisplay, identityDisplay, isRawRunnerId, replaceRunnerIds } from './identity-display';

const SA_ID = 'service-account:sa_62c9a37021db4c9e8f10';
const RUNNER_ID = 'runner_d9a38223247c41e4bab44778397c0794';

describe('identityDisplay', () => {
  it('shows the name of a service account token and keeps the full id only in the tooltip', () => {
    const view = identityDisplay(SA_ID, { tokenName: 'operator session' });
    expect(view.kind).toBe('service-account');
    expect(view.label).toBe('operator session');
    expect(view.fallback).toBe(false);
    expect(view.tooltip).toContain(SA_ID);
    expect(view.label).not.toContain('sa_62c9');
  });

  it('prefers the account name over the token name', () => {
    expect(identityDisplay(SA_ID, { name: 'CI bot', tokenName: 'operator session' }).label).toBe('CI bot');
  });

  it('falls back to kind plus the last four characters, never the raw id', () => {
    const view = identityDisplay(SA_ID);
    expect(view.fallback).toBe(true);
    expect(view.label).toBe('sa·8f10');
    expect(identityDisplay(SA_ID).label).toBe(view.label);
  });

  it('treats a name that only repeats the raw id as no name', () => {
    expect(identityDisplay(SA_ID, { name: SA_ID }).label).toBe('sa·8f10');
  });

  it('shows a person name with the person icon', () => {
    const view = identityDisplay('rainer-m', { name: 'Rainer M', kind: 'human' });
    expect(view.label).toBe('Rainer M');
    expect(view.kind).toBe('human');
  });
});

describe('host alias', () => {
  it('recognises runner_<hex> ids only', () => {
    expect(isRawRunnerId(RUNNER_ID)).toBe(true);
    expect(isRawRunnerId('agent-runner-01')).toBe(false);
  });

  it('uses the display name and keeps the id in the tooltip', () => {
    const view = hostDisplay(RUNNER_ID, { name: 'agent-runner-01' });
    expect(view.label).toBe('agent-runner-01');
    expect(view.generated).toBe(false);
    expect(view.tooltip).toContain(RUNNER_ID);
  });

  it('generates a stable readable alias for a host without a name', () => {
    const first = hostDisplay(RUNNER_ID, { role: 'review' });
    const second = hostDisplay(RUNNER_ID, { role: 'review' });
    expect(first.label).toBe(second.label);
    expect(first.label).toMatch(/^review-[a-z]+-\d{2}$/);
    expect(first.generated).toBe(true);
    expect(first.label).not.toContain('runner_');
    expect(generatedHostAlias('runner_00000000aaaa')).not.toBe(generatedHostAlias(RUNNER_ID));
  });

  it('ignores a display name that is a raw runner id, and lets an owner rename win', () => {
    expect(hostDisplay(RUNNER_ID, { name: RUNNER_ID }).generated).toBe(true);
    expect(hostDisplay(RUNNER_ID, { name: 'a', override: 'build box' }).label).toBe('build box');
  });

  it('keeps a readable id as its own name', () => {
    expect(hostDisplay('agent-runner-01').label).toBe('agent-runner-01');
  });

  it('replaces runner ids in log text', () => {
    const text = replaceRunnerIds(`claimed by ${RUNNER_ID} just now`);
    expect(text).not.toContain('runner_d9a3');
  });
});

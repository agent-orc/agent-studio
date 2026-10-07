import { describe, expect, it } from 'vitest';
import { followUp } from './fixtures/agt-2975-orchestrator-follow-up.fixture';
import { parseFollowUp } from './follow-up-parser';


describe('AGT-2975 orchestrator follow-up', () => {
  it('separates both worktree-blocked entries into fields, cause, and collapsed output', () => {
    const items = parseFollowUp(followUp);
    expect(items).toHaveLength(2);
    expect(items.map(item => item.kind)).toEqual(['worktree-blocked', 'worktree-blocked']);
    expect(items[0].fields.find(field => field.label === 'Host')?.value).toBe('agent-studio-runner');
    expect(items[0].fields.find(field => field.label === 'Canonical ref')?.value).toMatch(/^agent-studio\/salvage/);
    expect(items[0].fields.find(field => field.label === 'Retained local HEAD')?.display).toBe('4461bb25b');
    expect(items[1].fields.find(field => field.label === 'Branch')?.value).toContain('/AGT-2975/');
    expect(items[0].cause).toBe('GitHub push protection: secret detected (Mailgun API Key) at runner.Tests/LocalRepositoryKeyHostTests.cs:76');
    expect(items[0].rawOutput).toContain('GH013');
    expect(items[0].rawOutput).not.toContain('remote: remote:');
    expect(items[0].rawOutput).not.toMatch(/^[—─\s]+$/m);
  });

  it('also parses another labelled gate shape without worktree fields', () => {
    const [item] = parseFollowUp('- [ ] checkpoint-missing: repository=runner; ref=refs/heads/recovery/task; failure=permission denied');
    expect(item.fields.map(field => [field.label, field.value])).toEqual([
      ['Repository', 'runner'], ['Ref', 'recovery/task'],
    ]);
    expect(item.cause).toBe('Push failed: permission denied');
  });
});

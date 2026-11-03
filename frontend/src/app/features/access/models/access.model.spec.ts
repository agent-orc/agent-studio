import { describe, expect, it } from 'vitest';
import { ACCESS_PROJECTS, ACCESS_ROLES, accessOverview } from '../testing/access-fixtures';
import { operatorSession } from '../testing/operator-fixtures';
import { bindableRoles, formatAccessTime, groupBindingsByScope, principalName, scopeLabel } from './access.model';
import { formatAuditTime, isOperatorAgentAccount, orderedSessions, shortDigest } from './operator-agent.model';

describe('access model', () => {
  it('groups bindings per scope with the workspace first and environments after their project', () => {
    const overview = accessOverview();
    const groups = groupBindingsByScope([...overview.bindings].reverse(), ACCESS_PROJECTS);
    expect(groups.map(group => group.key)).toEqual(['workspace', 'project:proj-001', 'environment:proj-001/production']);
    expect(scopeLabel(groups[2].scope, ACCESS_PROJECTS)).toBe('Website · production');
  });

  it('offers only the roles a principal kind may hold in a scope kind', () => {
    expect(bindableRoles(ACCESS_ROLES, 'user', 'workspace').map(role => role.id)).toEqual(['owner', 'admin', 'developer', 'viewer', 'approver']);
    expect(bindableRoles(ACCESS_ROLES, 'user', 'project').map(role => role.id)).toEqual(['admin', 'developer', 'contributor', 'viewer', 'approver']);
    expect(bindableRoles(ACCESS_ROLES, 'service-account', 'environment').map(role => role.id)).toEqual(['developer', 'viewer']);
  });

  it('names principals and formats times in UTC', () => {
    const overview = accessOverview();
    expect(principalName('group', 'grp_web', overview)).toBe('Team Website');
    expect(principalName('service-account', 'sa_missing', overview)).toBe('sa_missing');
    expect(formatAccessTime('2026-10-20T09:05:00Z')).toBe('20 Oct 2026, 09:05 UTC');
    expect(formatAccessTime(null)).toBe('');
  });

  it('shortens parameter digests, orders sessions active first, and recognises operator accounts', () => {
    expect(shortDigest('sha256-0123456789abcdef')).toBe('sha256-01234');
    expect(shortDigest(null)).toBe('');
    const sessions = orderedSessions([
      operatorSession({ id: 'old', state: 'expired', createdAt: '2026-11-02T09:00:00Z' }),
      operatorSession({ id: 'live', createdAt: '2026-11-01T09:00:00Z' }),
      operatorSession({ id: 'newer', state: 'revoked', createdAt: '2026-11-02T10:00:00Z' }),
    ]);
    expect(sessions.map(session => session.id)).toEqual(['live', 'newer', 'old']);
    expect(isOperatorAgentAccount({ origin: 'operator-agent' })).toBe(true);
    expect(isOperatorAgentAccount({ origin: 'api-token' })).toBe(false);
    expect(formatAuditTime('2026-11-02T08:30:05Z')).toBe('2 Nov 2026, 08:30:05 UTC');
  });
});

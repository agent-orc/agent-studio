import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  oneTimeOperatorSession,
  operatorActivity,
  operatorAgents,
  operatorDrill,
  operatorKill,
  operatorKillSwitch,
  operatorSession,
} from '../testing/operator-fixtures';
import { OPERATOR_ACTIVITY_LIMIT, OperatorAgentsService } from './operator-agents.service';

const OPERATORS = '/api/auth/iam/operator-agents';
const ACTIVITY = '/api/auth/audit/operator-activity';
const KILL_SWITCH = '/api/auth/iam/operator-kill-switch';

describe('OperatorAgentsService', () => {
  let service: OperatorAgentsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(OperatorAgentsService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i++) await Promise.resolve();
  }

  it('loads the operator agents and marks the sections available', async () => {
    const loading = service.load();
    http.expectOne(OPERATORS).flush(operatorAgents());
    await loading;
    expect(service.state()).toBe('ready');
    expect(service.available()).toBe(true);
    expect(service.agents().map(agent => agent.id)).toEqual(['opa_ops', 'opa_off']);
  });

  it.each([401, 403, 404])('hides the sections and clears cached values when the read answers %i', async status => {
    const first = service.load();
    http.expectOne(OPERATORS).flush(operatorAgents());
    await first;
    const activity = service.loadActivity();
    http.expectOne(request => request.url === ACTIVITY).flush(operatorActivity());
    await activity;

    const reloading = service.load();
    http.expectOne(OPERATORS).flush({ message: 'Only the Owner manages operator agents.' }, { status, statusText: 'Refused' });
    await reloading;
    expect(service.available()).toBe(false);
    expect(service.state()).toBe('denied');
    expect(service.agents()).toEqual([]);
    expect(service.activity()).toBeNull();
    expect(service.error()).toBeNull();
  });

  it('reads the activity with a limit and narrows it to one session', async () => {
    const all = service.loadActivity();
    const first = http.expectOne(request => request.url === ACTIVITY);
    expect(first.request.params.get('limit')).toBe(String(OPERATOR_ACTIVITY_LIMIT));
    expect(first.request.params.has('sessionId')).toBe(false);
    first.flush(operatorActivity());
    await all;
    expect(service.activity()?.entries.length).toBe(2);

    const narrowed = service.filterSession('ses_live');
    const second = http.expectOne(request => request.url === ACTIVITY);
    expect(second.request.params.get('sessionId')).toBe('ses_live');
    second.flush(operatorActivity({ entries: [] }));
    await narrowed;
    expect(service.sessionFilter()).toBe('ses_live');
    expect(service.activity()?.entries).toEqual([]);
  });

  it('drops a stale activity answer that arrives after a newer one', async () => {
    const stale = service.loadActivity();
    const fresh = service.filterSession('ses_live');
    const [older, newer] = http.match(request => request.url === ACTIVITY);
    newer.flush(operatorActivity({ entries: [] }));
    older.flush(operatorActivity());
    await Promise.all([stale, fresh]);
    expect(service.activity()?.entries).toEqual([]);
  });

  it('verifies the chain and replaces only the integrity of the shown activity', async () => {
    const loading = service.loadActivity();
    http.expectOne(request => request.url === ACTIVITY).flush(operatorActivity());
    await loading;

    const verifying = service.verify();
    expect(service.verifying()).toBe(true);
    const request = http.expectOne('/api/auth/audit/verify');
    expect(request.request.method).toBe('POST');
    request.flush({ state: 'failed', checkedAt: '2026-11-02T09:00:00Z', verifiedThrough: 40, message: 'Hash mismatch at entry 41.', alert: true });
    expect((await verifying)?.state).toBe('failed');
    expect(service.verifying()).toBe(false);
    expect(service.activity()?.integrity.alert).toBe(true);
    expect(service.activity()?.entries.length).toBe(2);
  });

  it('creates an agent without an empty description and reloads the list', async () => {
    const creating = service.create('studio-operator', '');
    const request = http.expectOne(OPERATORS);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ name: 'studio-operator' });
    request.flush(operatorAgents()[0]);
    await settle();
    http.expectOne(OPERATORS).flush(operatorAgents());
    expect((await creating)?.id).toBe('opa_ops');
    expect(service.busy()).toBe(false);
  });

  it('starts and renews sessions on the agent routes and refreshes an open activity view', async () => {
    const opening = service.loadActivity();
    http.expectOne(request => request.url === ACTIVITY).flush(operatorActivity());
    await opening;

    const issued = oneTimeOperatorSession();
    const starting = service.startSession('opa_ops', '', 4);
    const start = http.expectOne(`${OPERATORS}/opa_ops/sessions`);
    expect(start.request.body).toEqual({ lifetimeHours: 4 });
    start.flush(issued);
    await settle();
    http.expectOne(OPERATORS).flush(operatorAgents());
    http.expectOne(request => request.url === ACTIVITY).flush(operatorActivity());
    expect((await starting)?.token).toBe(issued.token);

    const renewing = service.renewSession('opa_ops', 'ses_live', 8);
    const renew = http.expectOne(`${OPERATORS}/opa_ops/sessions/ses_live/renew`);
    expect(renew.request.method).toBe('POST');
    expect(renew.request.body).toEqual({ lifetimeHours: 8 });
    renew.flush(operatorSession({ renewedAt: '2026-11-02T09:00:00Z' }));
    await settle();
    http.expectOne(OPERATORS).flush(operatorAgents());
    http.expectOne(request => request.url === ACTIVITY).flush(operatorActivity());
    await renewing;
  });

  it('ends all access, kills one session, and runs a drill, reloading the agents and the switch each time', async () => {
    const ending = service.endAllAccess('Loop on restarts');
    const end = http.expectOne(`${KILL_SWITCH}/end-all`);
    expect(end.request.method).toBe('POST');
    expect(end.request.body).toEqual({ reason: 'Loop on restarts' });
    expect(service.killing()).toBe(true);
    end.flush(operatorKill());
    await settle();
    http.expectOne(OPERATORS).flush(operatorAgents());
    http.expectOne(KILL_SWITCH).flush(operatorKillSwitch());
    expect((await ending)?.id).toBe('kill_1');
    expect(service.killing()).toBe(false);
    expect(service.killSwitch()?.drills).toHaveLength(1);

    const killing = service.killSession('opa_ops', 'ses_live');
    const kill = http.expectOne(`${OPERATORS}/opa_ops/sessions/ses_live/kill`);
    expect(kill.request.method).toBe('POST');
    kill.flush(operatorKill({ kind: 'session', sessionIds: ['ses_live'] }));
    await settle();
    http.expectOne(OPERATORS).flush(operatorAgents());
    http.expectOne(KILL_SWITCH).flush(operatorKillSwitch());
    expect((await killing)?.kind).toBe('session');

    const drilling = service.runDrill();
    http.expectOne(`${KILL_SWITCH}/drills`).flush(operatorDrill());
    await settle();
    http.expectOne(OPERATORS).flush(operatorAgents());
    http.expectOne(KILL_SWITCH).flush(operatorKillSwitch());
    expect((await drilling)?.passed).toBe(true);
  });

  it('keeps the server message when the kill switch refuses a call', async () => {
    const ending = service.endAllAccess('');
    const end = http.expectOne(`${KILL_SWITCH}/end-all`);
    expect(end.request.body).toEqual({});
    end.flush({ message: 'Only the Owner manages operator agents and their sessions.' }, { status: 409, statusText: 'Conflict' });
    expect(await ending).toBeNull();
    expect(service.killSwitchError()).toBe('Only the Owner manages operator agents and their sessions.');
  });

  it('disables an agent and keeps the server message when a mutation fails', async () => {
    const disabling = service.disable('opa_ops');
    const request = http.expectOne(`${OPERATORS}/opa_ops`);
    expect(request.request.method).toBe('DELETE');
    request.flush({ message: 'Operator agent opa_ops was not found.' }, { status: 404, statusText: 'Not Found' });
    expect(await disabling).toBeNull();
    expect(service.error()).toBe('Operator agent opa_ops was not found.');
    expect(service.state()).not.toBe('denied');
  });
});

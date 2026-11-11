import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import type { TaskInfo } from '../../../models/task.model';
import { AuthSessionState, type AuthUser } from '../../../services/auth.service';
import { SessionEventsPollService } from './session-events-poll.service';

/** AGT-3669: a Product session never asks for the session-event log it is refused. */
describe('SessionEventsPollService · role access', () => {
  let http: HttpTestingController;
  const task = { id: 'card', taskKey: 'C:/demo::card', watchPath: 'C:/demo', state: '5-human-review',
    execution: null } as unknown as TaskInfo;
  const signIn = (role: AuthUser['role']) => TestBed.inject(AuthSessionState).status.set({
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'usr', username: role, displayName: role, role, projects: [], disabled: false, mustChangePassword: false },
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), SessionEventsPollService],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
  });

  it('skips the read for a Product session', () => {
    signIn('contributor');
    const poll = TestBed.inject(SessionEventsPollService);
    poll.syncTo(task);

    http.expectNone(req => req.url.endsWith('/session-events'));
    expect(poll.response()).toBeNull();
    poll.stop();
  });

  it('reads the log for an Operator session', () => {
    signIn('operator');
    const poll = TestBed.inject(SessionEventsPollService);
    poll.syncTo(task);

    http.expectOne(req => req.url.endsWith('/card/session-events')).flush({ events: [], sessionChain: [] });
    expect(poll.response()).toEqual({ events: [], sessionChain: [] });
    poll.stop();
  });
});

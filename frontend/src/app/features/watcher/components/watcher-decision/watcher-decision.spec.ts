import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { WatcherDecisionComponent, isWatcherDecisionEntry } from './watcher-decision';
import { WATCHER_PARTICIPANT_ID, WATCHER_TOPICS } from '../../models/watcher.model';
import type { WatcherProposal } from '../../models/watcher.model';

const CASE_ID = 'WCH-unit0fixture';

function proposal(overrides: Partial<WatcherProposal> = {}): WatcherProposal {
  return {
    id: 'WPR-unit0fixture',
    caseId: CASE_ID,
    fingerprint: 'hygiene|dossier-descriptor',
    fingerprintDigest: 'abc12345',
    detectorClass: 'hygiene',
    detectorRule: 'Validation errors are older than a grace period.',
    project: null,
    kind: 'new-card',
    draft: {
      title: '15 unresolved descriptor validation errors',
      promptMarkdown: '## Context',
      taskType: 'chore',
      tags: ['watcher-proposal'],
      relatedTo: [],
    },
    recommendation: {
      tier: 'luna-medium', model: 'gpt-5.6-luna', thinkingLevel: 'medium',
      policyVersion: '2026-07-24', score: 15, correctnessFloorTier: null, reason: 'policy',
    },
    evidenceDigest: 'd41d8cd98f00b204',
    evidence: [],
    modelCalls: [],
    createdTaskKey: 'AGT-9001',
    commentedOnTaskKey: null,
    decision: {
      state: 'pending', decidedAtUtc: null, decidedBy: null,
      reason: null, mergedIntoTaskKey: null,
    },
    createdAtUtc: '2026-09-06T20:05:00Z',
    updatedAtUtc: '2026-09-06T20:05:00Z',
    ...overrides,
  };
}

async function build(proposals: WatcherProposal[] = [proposal()]) {
  await TestBed.configureTestingModule({
    imports: [WatcherDecisionComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(WatcherDecisionComponent);
  fixture.componentRef.setInput('caseId', CASE_ID);
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  http.expectOne('/api/watcher/proposals').flush({ proposals });
  fixture.detectChanges();
  return { fixture, http };
}

describe('WatcherDecisionComponent', () => {
  it('loads the proposal that belongs to the case behind the feed row', async () => {
    const { fixture } = await build();

    expect(fixture.componentInstance.proposal()?.caseId).toBe(CASE_ID);
    expect(fixture.componentInstance.pending()).toBe(true);
    expect(fixture.componentInstance.targetCard()).toBe('AGT-9001');
  });

  it('reports a case with no proposal without pretending the load failed', async () => {
    const { fixture } = await build([]);

    expect(fixture.componentInstance.missing()).toBe(true);
    expect(fixture.componentInstance.error()).toBeNull();
  });

  it('distinguishes a failed load from a case that simply has no proposal', async () => {
    await TestBed.configureTestingModule({
      imports: [WatcherDecisionComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WatcherDecisionComponent);
    fixture.componentRef.setInput('caseId', CASE_ID);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/watcher/proposals')
      .error(new ProgressEvent('network'));
    fixture.detectChanges();

    expect(fixture.componentInstance.missing()).toBe(false);
    expect(fixture.componentInstance.error()).not.toBeNull();
  });

  it('refuses to confirm a rejection until a reason is typed', async () => {
    const { fixture } = await build();
    const component = fixture.componentInstance;

    component.start('rejected');
    expect(component.canSubmit()).toBe(false);

    // Whitespace is not a reason: it could not feed a suppression an operator
    // would later understand.
    component.reasonDraft.set('   ');
    expect(component.canSubmit()).toBe(false);

    component.reasonDraft.set('planned maintenance window');
    expect(component.canSubmit()).toBe(true);
  });

  it('refuses to confirm a merge until a target card is named', async () => {
    const { fixture } = await build();
    const component = fixture.componentInstance;

    component.start('merged');
    expect(component.canSubmit()).toBe(false);

    component.mergeTargetDraft.set('AGT-2717');
    expect(component.canSubmit()).toBe(true);
  });

  it('allows an approval with no further input', async () => {
    const { fixture } = await build();

    fixture.componentInstance.start('approved');

    expect(fixture.componentInstance.canSubmit()).toBe(true);
  });

  it('posts the answer and keeps the recorded decision', async () => {
    const { fixture, http } = await build();
    const component = fixture.componentInstance;
    const decided: WatcherProposal[] = [];
    component.decided.subscribe(item => decided.push(item));

    component.start('approved');
    component.submit();

    const request = http.expectOne('/api/watcher/proposals/WPR-unit0fixture/decision');
    expect(request.request.body).toMatchObject({ decision: 'approved' });
    request.flush(proposal({
      decision: {
        state: 'approved', decidedAtUtc: '2026-09-06T20:10:00Z',
        decidedBy: 'alice', reason: null, mergedIntoTaskKey: null,
      },
    }));
    fixture.detectChanges();

    expect(component.pending()).toBe(false);
    expect(component.answer()).toBeNull();
    expect(decided).toHaveLength(1);
  });

  it('clears the drafts when the operator cancels', async () => {
    const { fixture } = await build();
    const component = fixture.componentInstance;

    component.start('rejected');
    component.reasonDraft.set('never mind');
    component.cancel();

    expect(component.answer()).toBeNull();
    expect(component.reasonDraft()).toBe('');
  });

  it('recognises only a pending Watcher proposal row as answerable here', () => {
    const row = {
      participantId: WATCHER_PARTICIPANT_ID,
      topic: WATCHER_TOPICS.decisionRequired,
      correlationId: CASE_ID,
    };

    expect(isWatcherDecisionEntry(row)).toBe(true);
    // Another producer's decision row keeps the existing steer override.
    expect(isWatcherDecisionEntry({ ...row, participantId: 'orchestrator' })).toBe(false);
    expect(isWatcherDecisionEntry({ ...row, topic: WATCHER_TOPICS.findingRaised })).toBe(false);
    // Without a correlation id there is no case to answer.
    expect(isWatcherDecisionEntry({ ...row, correlationId: null })).toBe(false);
  });
});

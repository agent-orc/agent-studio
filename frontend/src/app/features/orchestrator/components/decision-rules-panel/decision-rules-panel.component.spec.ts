import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TaskService } from '../../../../services/task.service';
import { DECISION_RULE_TRIGGERS, DecisionRulesPanelComponent } from './decision-rules-panel.component';

const workspace = {
  id: 'ws-test', displayName: 'Test workspace', sortOrder: 0, isDefault: true,
  color: null, createdAt: '2026-10-15T10:00:00Z', projects: [],
};
const rulesUrl = '/api/workspaces/ws-test/decision-rules';
const receiptsUrl = '/api/orchestration/decision-receipts?workspaceId=ws-test&limit=20';
const spendUrl = '/api/orchestration/decision-receipts/spend?workspaceId=ws-test';

class TaskServiceStub {
  workspaceCalls = 0;
  getRegistryWorkspaces() {
    this.workspaceCalls++;
    return of([workspace]);
  }
}

async function settle(): Promise<void> {
  for (let index = 0; index < 5; index++) await new Promise(resolve => setTimeout(resolve, 0));
}

describe('DecisionRulesPanelComponent', () => {
  let http: HttpTestingController;
  let tasks: TaskServiceStub;

  beforeEach(() => {
    tasks = new TaskServiceStub();
    TestBed.configureTestingModule({
      imports: [DecisionRulesPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskService, useValue: tasks },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render(view: object) {
    const fixture = TestBed.createComponent(DecisionRulesPanelComponent);
    fixture.detectChanges();
    await settle();
    http.expectOne(rulesUrl).flush(view);
    await settle();
    http.expectOne(receiptsUrl).flush([
      { receiptId: 'odr_host', taskId: null, hostId: 'runner_1', profile: 'capacity-check', chosenAction: 'observe',
        model: 'gpt-6-sol', inputTokens: 100, outputTokens: 20, estimatedUsd: 0.01, evidenceRef: '/r/odr_host' },
      { receiptId: 'odr_card', taskId: 'AGT-1', hostId: null, profile: 'CodingCompletion', chosenAction: 'prepare',
        model: 'gpt-6-sol', inputTokens: 100, outputTokens: 20, estimatedUsd: 0.01, evidenceRef: '/r/odr_card' },
    ]);
    http.expectOne(spendUrl).flush({ occurrences: 2, inputTokens: 200, outputTokens: 40, pricedUsd: 0.02, unknownPriceCalls: 0 });
    await settle();
    fixture.detectChanges();
    return fixture;
  }

  it('loads workspaces through the task service and lists only card-free host history', async () => {
    const fixture = await render({ activeVersionId: null, versions: [], hardFloors: [] });
    expect(tasks.workspaceCalls).toBe(1);
    http.expectNone('/api/workspaces');
    expect(fixture.componentInstance.hostHistory().map(row => row.receiptId)).toEqual(['odr_host']);
    expect(fixture.nativeElement.textContent).toContain('platform defaults');
    expect(fixture.nativeElement.querySelector('[data-testid="decision-rules-degraded"]')).toBeNull();
  });

  it('shows a degraded rule store and its quarantined records', async () => {
    const fixture = await render({ activeVersionId: null, versions: [], hardFloors: [],
      degraded: true, quarantined: ['version-drv_bad.json'] });
    const alert = fixture.nativeElement.querySelector('[data-testid="decision-rules-degraded"]');
    expect(alert?.textContent).toContain('version-drv_bad.json');
  });

  it('shows the typed proxy-profile refusal', async () => {
    const fixture = TestBed.createComponent(DecisionRulesPanelComponent);
    fixture.detectChanges();
    await settle();
    http.expectOne(rulesUrl).flush(
      { code: 'decision-rules-not-on-proxy-profile', message: 'The standalone Task Server owns workspaces.' },
      { status: 409, statusText: 'Conflict' });
    await settle();
    expect(fixture.componentInstance.error()).toBe('The standalone Task Server owns workspaces.');
  });

  it('shows an error when host history or spend cannot load', async () => {
    const fixture = TestBed.createComponent(DecisionRulesPanelComponent);
    fixture.detectChanges();
    await settle();
    http.expectOne(rulesUrl).flush({ activeVersionId: null, versions: [], hardFloors: [] });
    await settle();
    http.expectOne(receiptsUrl).flush({ message: 'Receipt store unavailable.' },
      { status: 503, statusText: 'Service Unavailable' });
    http.expectOne(spendUrl).flush({}, { status: 500, statusText: 'Server Error' });
    await settle();
    fixture.detectChanges();
    const alert = fixture.nativeElement.querySelector('[data-testid="decision-rules-history-error"]');
    expect(alert?.textContent).toContain('Receipt store unavailable.');
    expect(alert?.textContent).toContain('Could not load workspace decision spend.');
    expect(fixture.componentInstance.error()).toBeNull();
  });

  it('drafts a new rule from the closed trigger list with the configured model', async () => {
    const fixture = await render({ activeVersionId: null, versions: [], hardFloors: [] });
    const component = fixture.componentInstance;
    component.addRule();
    fixture.detectChanges();
    const options = [...fixture.nativeElement.querySelectorAll('.rules__row select')[0].options]
      .map((option: HTMLOptionElement) => option.value);
    expect(options).toEqual([...DECISION_RULE_TRIGGERS]);
    component.updateRule(0, 'id', 'queue-pressure');
    component.updateRule(0, 'factId', 'host.queue');
    component.reason.set('Watch queue pressure');
    const draft = component.draft();
    await settle();
    const request = http.expectOne(`${rulesUrl}/versions`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body.rules).toEqual([expect.objectContaining({
      id: 'queue-pressure', trigger: 'capacity-check', factId: 'host.queue', model: null,
    })]);
    request.flush({ versionId: 'drv_new', parentVersionId: null, actor: 'operator', reason: 'Watch queue pressure',
      diff: 'queue-pressure', createdAt: '2026-10-15T10:00:00Z', rules: request.request.body.rules });
    await draft;
    expect(component.draftId()).toBe('drv_new');
  });
});

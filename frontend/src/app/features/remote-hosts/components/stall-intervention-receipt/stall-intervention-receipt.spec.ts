import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { StallInterventionReceiptComponent } from './stall-intervention-receipt';
import type { StallInterventionRow } from '../../models/stall-intervention.model';

const row = (undoable: boolean, status = 'applied'): StallInterventionRow => ({
  receipt: {
    id: 'si-1', subject: 'host:agent-runner-01', action: 'lower-admission', autonomy: 'A1', status,
    observed: 'agent-runner-01 ran with load 46.7 on 12 cores, against a 95 % target.',
    done: 'I lowered its admission from 8 to 6 parallel runs.',
    why: 'Fewer parallel builds bring the load back under the target.',
    before: 'admission 8, 8 runs active', after: 'admission 6; new work waits, running work continues',
    undo: 'Undo restores 8 (Settings > Capacity, or Undo on this receipt).', chain: [], undoable,
  },
  kind: 'host-overload', host: 'agent-runner-01', project: '_workspace', decidedBy: 'monolith',
  decidedAtUtc: '2026-10-16T19:52:00Z', affectedCards: [], headline: 'AI Operator (A1) on host:agent-runner-01',
});

describe('StallInterventionReceiptComponent', () => {
  it('shows what was observed, done, changed and how to undo it, and emits undo', async () => {
    TestBed.configureTestingModule({
      imports: [StallInterventionReceiptComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(StallInterventionReceiptComponent);
    fixture.componentRef.setInput('row', row(true));
    const undone: string[] = [];
    fixture.componentInstance.undo.subscribe(id => undone.push(id));
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const text = element.textContent ?? '';
    expect(text).toContain('AI Operator · A1');
    expect(text).toContain('load 46.7 on 12 cores');
    expect(text).toContain('from 8 to 6');
    expect(text).toContain('admission 8, 8 runs active → admission 6; new work waits');
    expect(text).toContain('Settings > Capacity');
    expect(text).toContain('Applied, follow-up pending');
    (element.querySelector('[data-testid="stall-intervention-undo"]') as HTMLButtonElement).click();
    expect(undone).toEqual(['si-1']);
  });

  it('offers no undo once an intervention is settled', async () => {
    TestBed.configureTestingModule({
      imports: [StallInterventionReceiptComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(StallInterventionReceiptComponent);
    fixture.componentRef.setInput('row', row(false, 'verified'));
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="stall-intervention-undo"]')).toBeNull();
    expect(element.textContent).toContain('Verified by its effect');
  });
});

import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { OperatorBatchSheetComponent } from './operator-batch-sheet';
import { OperatorBatchesService } from '../../services/operator-batches.service';
import type { OperatorBatch } from '../../models/operator-batch.model';

describe('Operator batch sheet', () => {
  it('selects qualifying items by default and sends only the Owner selection with one factor', async () => {
    const batch: OperatorBatch = { id: 'opb_test', actor: 'operator-agent:ops/session:s1', class: 'cards.accept-override',
      instructionSource: 'owner-chat', expiresAt: '2026-11-28T12:00:00Z', state: 'pending',
      items: [0, 1, 2].map(index => ({ index, title: `Card ${index}`, request: { card: `TEST-${index}`, project: 'demo',
        targetLane: '6-completed', reason: 'Owner asked' }, reservation: 'Naming concern', refusal: index === 2 ? 'Review missing' : null,
      state: 'pending', resultReason: null })) };
    const service = { batches: signal([batch]), busy: signal(false), error: signal(null),
      load: vi.fn(() => Promise.resolve()), decide: vi.fn(async () => true) };
    TestBed.configureTestingModule({ imports: [OperatorBatchSheetComponent], providers: [provideHttpClient(),
      provideHttpClientTesting(), provideRouter([]), { provide: OperatorBatchesService, useValue: service }] });
    const fixture = TestBed.createComponent(OperatorBatchSheetComponent);
    fixture.componentInstance.open(batch);
    fixture.detectChanges();
    const inputs = [...document.querySelectorAll('[data-testid=operator-batch-sheet] input[type=checkbox]')] as HTMLInputElement[];
    expect(inputs.map(input => input.checked)).toEqual([true, true, false]);
    expect(inputs[2].disabled).toBe(true);
    const link = document.querySelector('[data-testid=operator-batch-sheet] a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('#/tasks/TEST-0');
    expect(link.target).toBe('_blank');
    inputs[1].click();
    fixture.componentInstance.code.set('123456');
    await fixture.componentInstance.decide(true);
    expect(service.decide).toHaveBeenCalledExactlyOnceWith('opb_test', [0], true, '123456');
    expect(fixture.componentInstance.code()).toBe('');
    window.history.replaceState(null, '', '#/workspace/settings/access?batch=opb_test');
    fixture.componentInstance.close();
    expect(window.location.hash).toBe('#/workspace/settings/access');
    expect(fixture.componentInstance.focused()).toBeNull();
    expect(fixture.componentInstance.selected().size).toBe(0);
    window.history.replaceState(null, '', '/');
  });
});

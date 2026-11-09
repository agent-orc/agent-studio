import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import type { LaneCardGroup } from '../review-grouping.util';
import { LaneGroupHeadComponent } from './lane-group-head.component';

const card = (id: string) => ({ id, taskKey: `demo::${id}`, title: id, state: '5e-escalated', watchPath: '/tmp/demo' }) as TaskInfo;

async function render(group: LaneCardGroup) {
  await TestBed.configureTestingModule({
    imports: [LaneGroupHeadComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  }).compileComponents();
  const fixture = TestBed.createComponent(LaneGroupHeadComponent);
  fixture.componentRef.setInput('group', group);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('LaneGroupHeadComponent', () => {
  it('shows a review split as label and count without actions', async () => {
    const root = await render({ key: 'human', kind: 'human', label: 'Human review', icon: '👤', testid: 'review-subsection-human', jobs: [card('a')] });
    expect(root.querySelector('[data-testid="lane-group-label"]')?.textContent?.trim()).toBe('Human review');
    expect(root.querySelector('[data-testid="lane-group-count"]')?.textContent?.trim()).toBe('1');
    expect(root.querySelector('app-bulk-lane-actions')).toBeNull();
  });

  it('shows an escalation burst with its cause, start time and bulk actions (AGT-3585)', async () => {
    const root = await render({
      key: 'k', kind: 'burst', label: 'Missing Delivery', icon: '', testid: 'escalation-burst', jobs: [card('a'), card('b')],
      burst: { cause: 'missing-delivery', since: '2026-11-09T09:23:00.000Z' },
    });
    const head = root.querySelector<HTMLElement>('[data-testid="lane-group-head"]')!;
    expect(head.dataset['cause']).toBe('missing-delivery');
    expect(head.textContent).toContain('2 cards escalated for this cause since');
    expect(root.querySelector('[data-testid="lane-group-since"]')?.getAttribute('datetime')).toBe('2026-11-09T09:23:00.000Z');
    expect(root.querySelectorAll('[data-testid^="bulk-action-"]')).toHaveLength(2);
  });
});

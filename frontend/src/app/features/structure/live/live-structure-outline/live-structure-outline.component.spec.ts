import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { liveSourcesFixture, liveStructureFixture } from '../live-structure.fixtures';
import { buildLiveScene } from '../live-structure.model';
import { LiveStructureOutlineComponent } from './live-structure-outline.component';

describe('LiveStructureOutlineComponent', () => {
  it('lists every node of the scene nested, marks the selection, selects on click and zooms on the second click', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const scene = buildLiveScene(liveStructureFixture, liveSourcesFixture);
    const fixture = TestBed.createComponent(LiveStructureOutlineComponent);
    fixture.componentRef.setInput('scene', scene);
    fixture.componentRef.setInput('selected', 'machine:review-3');
    const chosen: string[] = [], entered: string[] = [];
    fixture.componentInstance.choose.subscribe(id => chosen.push(id));
    fixture.componentInstance.enter.subscribe(id => entered.push(id));
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const rows = [...root.querySelectorAll<HTMLButtonElement>('[data-outline-id]')];
    expect(rows.length).toBe(scene.order.length - 1);
    const row = (id: string): HTMLButtonElement => rows.find(item => item.dataset['outlineId'] === id)!;
    expect(row('machine:review-3').getAttribute('aria-current')).toBe('true');
    expect(rows.filter(item => item.getAttribute('aria-current')).length).toBe(1);
    expect(row('machine:review-3').getAttribute('aria-label')).toBe('Review host 3, Container, Up, 2 warnings');
    expect(row('machine:review-3').textContent).toContain('0/2');
    const indent = (id: string): string => row(id).querySelector<HTMLElement>('.outline__indent')!.style.width;
    expect([indent('machine:home-lab'), indent('machine:linux-vm'), indent('machine:review-3'), indent('component:review-3:review-3-runner')])
      .toEqual(['0px', '14px', '28px', '42px']);
    row('machine:gate-1').click();
    row('machine:review-3').click();
    expect(chosen).toEqual(['machine:gate-1']);
    expect(entered).toEqual(['machine:review-3']);
  });
});

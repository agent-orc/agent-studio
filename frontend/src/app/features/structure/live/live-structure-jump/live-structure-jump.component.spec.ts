import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { liveSourcesFixture, liveStructureFixture } from '../live-structure.fixtures';
import { buildLiveScene } from '../live-structure.model';
import { LiveStructureJumpComponent } from './live-structure-jump.component';

function setup() {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(LiveStructureJumpComponent);
  fixture.componentRef.setInput('scene', buildLiveScene(liveStructureFixture, liveSourcesFixture));
  const jumps: string[] = [];
  let left = 0;
  fixture.componentInstance.jump.subscribe(id => jumps.push(id));
  fixture.componentInstance.left.subscribe(() => left++);
  fixture.detectChanges();
  const root = fixture.nativeElement as HTMLElement;
  const field = root.querySelector<HTMLInputElement>('[data-testid="live-jump-field"]')!;
  const type = (value: string): void => { field.value = value; field.dispatchEvent(new Event('input')); fixture.detectChanges(); };
  const key = (name: string): void => { field.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true })); fixture.detectChanges(); };
  const options = (): HTMLElement[] => [...root.querySelectorAll<HTMLElement>('[data-testid="live-jump-option"]')];
  return { fixture, root, field, type, key, options, jumps, leftCount: () => left };
}

describe('LiveStructureJumpComponent', () => {
  it('lists the nodes that match what is typed, with kind and place, and jumps to the first with Enter', () => {
    const { field, type, key, options, jumps, leftCount } = setup();
    expect(field.getAttribute('role')).toBe('combobox');
    expect(field.getAttribute('aria-expanded')).toBe('false');
    type('review 3');
    expect(field.getAttribute('aria-expanded')).toBe('true');
    expect(options()[0].textContent).toContain('Review host 3');
    expect(options()[0].textContent).toContain('Container · Gate and review machine / Gate and review VM');
    expect(options()[0].getAttribute('aria-selected')).toBe('true');
    expect(field.getAttribute('aria-activedescendant')).toBe(options()[0].id);
    key('Enter');
    expect(jumps).toEqual(['machine:review-3']);
    expect(leftCount()).toBe(1);
    expect(field.value).toBe('');
    expect(options()).toEqual([]);
  });

  it('moves through the matches with the arrow keys, picks with a click, and closes with Escape', () => {
    const { root, field, type, key, options, jumps, leftCount } = setup();
    type('review host');
    expect(options().length).toBeGreaterThanOrEqual(3);
    key('ArrowDown');
    expect(options()[1].getAttribute('aria-selected')).toBe('true');
    key('ArrowUp');
    key('ArrowUp');
    expect(options().at(-1)!.getAttribute('aria-selected')).toBe('true');
    const second = options()[1].dataset['nodeId']!;
    options()[1].click();
    expect(jumps).toEqual([second]);
    type('nothing like this');
    expect(root.querySelector('[data-testid="live-jump-none"]')!.textContent).toContain('No node matches');
    key('Enter');
    expect(jumps.length).toBe(1);
    key('Escape');
    expect(field.getAttribute('aria-expanded')).toBe('false');
    expect(leftCount()).toBe(2);
  });
});

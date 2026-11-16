import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { StudioTabOverflowComponent } from './studio-tab-overflow';

describe('StudioTabOverflowComponent', () => {
  async function render() {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(StudioTabOverflowComponent);
    fixture.componentRef.setInput('entries', [
      { key: 'board:__all__', label: 'All projects · Board' },
      { key: 'feed', label: 'Activity across projects' },
    ]);
    fixture.componentRef.setInput('activeKey', 'feed');
    await fixture.whenStable();
    return fixture;
  }

  it('names the open tab count and marks the active tab in its menu', async () => {
    const fixture = await render();
    const trigger = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="studio-tab-overflow"]')!;
    expect(trigger.getAttribute('aria-label')).toBe('Open tabs: 2');
    expect(fixture.componentInstance.items()).toEqual([
      expect.objectContaining({ id: 'board:__all__', label: 'All projects · Board', active: false }),
      expect.objectContaining({ id: 'feed', label: 'Activity across projects', active: true }),
    ]);
  });

  it('selects the picked tab and closes', async () => {
    const fixture = await render();
    const picked: string[] = [];
    fixture.componentInstance.tabSelect.subscribe(key => picked.push(key));
    fixture.componentInstance.open.set(true);
    const item = fixture.componentInstance.items()[0];
    if (item.kind !== 'row') throw new Error('Expected a tab row.');
    fixture.componentInstance.onItem({ id: item.id, item });
    expect(picked).toEqual(['board:__all__']);
    expect(fixture.componentInstance.open()).toBe(false);
  });
});

import { Component, ChangeDetectionStrategy } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, it, expect, vi } from 'vitest';
import { LegacyTooltipDirective, STUDIO_TOOLTIP_RENDERER } from './legacy-tooltip.directive';

@Component({
  selector: 'app-tooltip-bridge-test',
  imports: [LegacyTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './legacy-tooltip.directive.spec.html',
})
class TooltipBridgeTestComponent {}

describe('LegacyTooltipDirective host renderer bridge', () => {
  it('delegates the existing content, placement and severity, and cleans up on destroy', () => {
    const renderer = { show: vi.fn(), hide: vi.fn() };
    TestBed.configureTestingModule({ imports: [TooltipBridgeTestComponent], providers: [{ provide: STUDIO_TOOLTIP_RENDERER, useValue: renderer }] });
    const fixture = TestBed.createComponent(TooltipBridgeTestComponent);
    fixture.detectChanges();
    const button: HTMLButtonElement = fixture.nativeElement.querySelector('button');
    button.dispatchEvent(new MouseEvent('mouseenter'));
    expect(renderer.show).toHaveBeenCalledWith(button, 'Existing tooltip', 'bottom', 'warn');
    button.dispatchEvent(new MouseEvent('mouseleave'));
    expect(renderer.hide).toHaveBeenCalledWith(button);
    fixture.destroy();
    expect(renderer.hide).toHaveBeenCalledTimes(2);
  });
});

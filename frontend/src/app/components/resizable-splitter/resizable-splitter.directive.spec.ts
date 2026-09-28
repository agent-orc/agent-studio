import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ResizableSplitterDirective } from './resizable-splitter.directive';

@Component({
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ResizableSplitterDirective],
  templateUrl: './resizable-splitter.host.html',
})
class HostComponent {}

function setup(): { fixture: ComponentFixture<HostComponent>; host: HTMLElement; splitter: HTMLElement } {
  localStorage.removeItem('test.split');
  TestBed.configureTestingModule({ imports: [HostComponent] });
  const fixture = TestBed.createComponent(HostComponent);
  const host = fixture.nativeElement.querySelector('.host') as HTMLElement;
  Object.defineProperty(host, 'getBoundingClientRect', { value: () => ({ left: 0, right: 1000, width: 1000 }) });
  fixture.detectChanges();
  return { fixture, host, splitter: host.children[1] as HTMLElement };
}

describe('ResizableSplitterDirective', () => {
  it('drags, clamps and persists its size', () => {
    const { host, splitter } = setup();
    Object.assign(splitter, { setPointerCapture: () => undefined, releasePointerCapture: () => undefined });
    splitter.dispatchEvent(new PointerEvent('pointerdown', { pointerId: 1, clientX: 300, button: 0 }));
    splitter.dispatchEvent(new PointerEvent('pointermove', { pointerId: 1, clientX: 900 }));
    splitter.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1, clientX: 900 }));
    expect(host.style.getPropertyValue('--size')).toBe('679px');
    expect(localStorage.getItem('test.split')).toBe('679');
  });

  it('supports arrows and double-click reset', () => {
    const { host, splitter } = setup();
    splitter.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    expect(host.style.getPropertyValue('--size')).toBe('316px');
    splitter.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    expect(host.style.getPropertyValue('--size')).toBe('300px');
    expect(splitter.getAttribute('aria-valuenow')).toBe('300');
  });
});

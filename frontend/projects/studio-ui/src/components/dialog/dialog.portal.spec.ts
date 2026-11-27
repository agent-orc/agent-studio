/* eslint-disable @angular-eslint/prefer-on-push-component-change-detection, @angular-eslint/component-max-inline-declarations */
import { describe, expect, it } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { DialogComponent } from './dialog.component';

/**
 * Verifies the `portalToBody` relocation that fixes the lane-info modal
 * being clipped off-screen: when an ancestor establishes a containing
 * block for fixed descendants (here a `.column`-like wrapper with
 * `contain: layout paint`), the dialog must escape to the central
 * body-level overlay root so its `position: fixed` overlay is positioned
 * against the viewport.
 *
 * jsdom does no layout, so we assert the DOM relocation contract — which
 * is the part this change owns — not pixel geometry.
 */
@Component({
  standalone: true,
  imports: [DialogComponent],
  template: `
    <div class="contain-host">
      @if (open()) {
        <app-dialog [portalToBody]="portal()" [testid]="'spec-modal'">
          <p class="spec-body">hello</p>
        </app-dialog>
      }
    </div>
  `,
})
class HostComponent {
  readonly open = signal(true);
  readonly portal = signal(true);
}

function setup(portal: boolean) {
  TestBed.configureTestingModule({
    imports: [HostComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(HostComponent);
  fixture.componentInstance.portal.set(portal);
  fixture.detectChanges();
  return fixture;
}

describe('DialogComponent portalToBody', () => {
  it('relocates the dialog host to the body overlay root, out of the contain ancestor', () => {
    const fixture = setup(true);
    const host = document.querySelector('app-dialog') as HTMLElement;
    const root = document.body.querySelector('.studio-overlay-root') as HTMLElement;
    expect(host).toBeTruthy();
    expect(root).toBeTruthy();
    expect(host.parentElement).toBe(root);
    expect(root.parentElement).toBe(document.body);

    const wrapper = fixture.nativeElement.querySelector('.contain-host') as HTMLElement;
    expect(wrapper.querySelector('app-dialog')).toBeNull();

    // Projected content travels with the relocated host.
    expect(root.querySelector('app-dialog .spec-body')?.textContent).toBe('hello');

    // The overlay (the position:fixed element) lives under the relocated host.
    expect(root.querySelector('app-dialog .dialog__overlay')).toBeTruthy();

    fixture.destroy();
  });

  it('removes the relocated host from the body overlay root on destroy', () => {
    const fixture = setup(true);
    expect(document.querySelectorAll('app-dialog').length).toBe(1);
    fixture.componentInstance.open.set(false);
    fixture.detectChanges();
    expect(document.querySelectorAll('app-dialog').length).toBe(0);
    fixture.destroy();
    expect(document.querySelectorAll('app-dialog').length).toBe(0);
  });

  it('leaves the dialog in place when portalToBody is false (default behaviour preserved)', () => {
    const fixture = setup(false);
    const wrapper = fixture.nativeElement.querySelector('.contain-host') as HTMLElement;
    const host = document.querySelector('app-dialog') as HTMLElement;
    expect(host).toBeTruthy();
    // Stays nested in the contain ancestor; not hoisted to the body overlay root.
    expect(wrapper.contains(host)).toBe(true);
    expect(host.parentElement).not.toBe(document.body.querySelector('.studio-overlay-root'));
    fixture.destroy();
  });
});

/* eslint-disable @angular-eslint/prefer-on-push-component-change-detection, @angular-eslint/component-max-inline-declarations */
import { afterEach, describe, expect, it } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { DialogComponent } from './dialog.component';

/**
 * Keyboard contract of the shared modal (AGT-3831): Tab is contained by
 * focus-trap anchors around the panel, focus moves into the panel on open,
 * and closing returns focus to the opener instead of the body. jsdom does
 * no layout, so the Tab walk itself is covered by
 * e2e/shell/dialog-focus-keyboard.spec.ts.
 */
@Component({
  standalone: true,
  imports: [DialogComponent],
  template: `
    <button type="button" data-testid="opener">Open</button>
    <button type="button" data-testid="elsewhere">Elsewhere</button>
    @if (open()) {
      <app-dialog title="Spec dialog" testid="spec-modal">
        <button type="button" data-testid="inside">Inside</button>
      </app-dialog>
    }
  `,
})
class HostComponent {
  readonly open = signal(false);
}

function setup() {
  TestBed.configureTestingModule({
    imports: [HostComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(HostComponent);
  fixture.detectChanges();
  const opener = fixture.nativeElement.querySelector('[data-testid="opener"]') as HTMLButtonElement;
  opener.focus();
  return { fixture, opener };
}

async function openDialog(fixture: ReturnType<typeof setup>['fixture']): Promise<HTMLElement> {
  fixture.componentInstance.open.set(true);
  fixture.detectChanges();
  await fixture.whenStable();
  return document.querySelector<HTMLElement>('[data-testid="spec-modal"]')!;
}

describe('DialogComponent focus', () => {
  let fixture: ReturnType<typeof setup>['fixture'] | null = null;
  afterEach(() => { fixture?.destroy(); fixture = null; });

  it('is a named modal that takes focus and wraps Tab with trap anchors', async () => {
    const env = setup();
    fixture = env.fixture;
    const panel = await openDialog(fixture);
    expect(panel.getAttribute('aria-modal')).toBe('true');
    expect(panel.getAttribute('aria-label')).toBe('Spec dialog');
    expect(panel.contains(document.activeElement)).toBe(true);
    expect(panel.previousElementSibling?.classList.contains('cdk-focus-trap-anchor')).toBe(true);
    expect(panel.nextElementSibling?.classList.contains('cdk-focus-trap-anchor')).toBe(true);
  });

  it('returns focus to the opener when it closes', async () => {
    const env = setup();
    fixture = env.fixture;
    const panel = await openDialog(fixture);
    panel.querySelector<HTMLButtonElement>('[data-testid="inside"]')!.focus();
    fixture.componentInstance.open.set(false);
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="spec-modal"]')).toBeNull();
    expect(document.activeElement).toBe(env.opener);
  });

  it('keeps a focus the owner moved elsewhere before closing', async () => {
    const env = setup();
    fixture = env.fixture;
    await openDialog(fixture);
    const elsewhere = fixture.nativeElement.querySelector('[data-testid="elsewhere"]') as HTMLButtonElement;
    elsewhere.focus();
    fixture.componentInstance.open.set(false);
    fixture.detectChanges();
    expect(document.activeElement).toBe(elsewhere);
  });
});

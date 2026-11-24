import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { InlineConfirmComponent } from './inline-confirm.component';

const settle = () => new Promise<void>(resolve => queueMicrotask(resolve));

describe('InlineConfirmComponent', () => {
  function mount() {
    TestBed.configureTestingModule({ imports: [InlineConfirmComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(InlineConfirmComponent);
    fixture.componentRef.setInput('label', 'Revoke');
    fixture.componentRef.setInput('confirmLabel', 'Revoke token');
    fixture.componentRef.setInput('message', 'Scripts that use this token stop working at once.');
    fixture.componentRef.setInput('testId', 'revoke');
    document.body.append(fixture.nativeElement);
    fixture.detectChanges();
    const confirmed: number[] = [];
    fixture.componentInstance.confirmed.subscribe(() => confirmed.push(1));
    return { fixture, root: fixture.nativeElement as HTMLElement, confirmed };
  }
  const click = (root: HTMLElement, id: string) => (root.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement).click();

  it('does nothing destructive on the first press: it states the consequence and focuses Cancel', async () => {
    const { fixture, root, confirmed } = mount();
    expect(root.querySelector('[data-testid="revoke-panel"]')).toBeNull();

    click(root, 'revoke');
    fixture.detectChanges();
    await settle();

    expect(confirmed).toEqual([]);
    expect(root.querySelector('[data-testid="revoke-panel"]')!.textContent).toContain('stop working at once');
    expect(root.querySelector('[data-testid="revoke-confirm"]')!.textContent).toBe('Revoke token');
    expect(document.activeElement).toBe(root.querySelector('[data-testid="revoke-cancel"]'));
    fixture.destroy();
  });

  it('emits once on confirm and returns to rest', async () => {
    const { fixture, root, confirmed } = mount();
    click(root, 'revoke');
    fixture.detectChanges();
    click(root, 'revoke-confirm');
    fixture.detectChanges();
    await settle();

    expect(confirmed).toEqual([1]);
    expect(root.querySelector('[data-testid="revoke-panel"]')).toBeNull();
    fixture.destroy();
  });

  it('cancels on Cancel and on Escape without emitting', async () => {
    const { fixture, root, confirmed } = mount();
    click(root, 'revoke');
    fixture.detectChanges();
    click(root, 'revoke-cancel');
    fixture.detectChanges();
    await settle();
    expect(root.querySelector('[data-testid="revoke-panel"]')).toBeNull();

    click(root, 'revoke');
    fixture.detectChanges();
    await settle();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    await settle();

    expect(root.querySelector('[data-testid="revoke-panel"]')).toBeNull();
    expect(confirmed).toEqual([]);
    fixture.destroy();
  });
});

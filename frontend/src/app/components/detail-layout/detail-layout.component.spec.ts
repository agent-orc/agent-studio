import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ModalStackService } from '../../services/modal-stack.service';
import { DetailLayoutComponent } from './detail-layout.component';

/* eslint-disable @angular-eslint/component-max-inline-declarations */
@Component({
  standalone: true,
  imports: [DetailLayoutComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-detail-layout [open]="open()" eyebrow="Service account" title="deploy-bot" subtitle="Stage deploys" [contextKey]="key()"
                       [footer]="true" (closeRequest)="open.set(false)">
      <button type="button" class="opener" (click)="open.set(true)">Open</button>
      <p detail class="facts">Facts</p>
      <button detailFooter type="button" class="danger">Disable</button>
    </app-detail-layout>`,
})
class HostComponent {
  readonly open = signal(false);
  readonly key = signal('a');
}

const settle = () => new Promise<void>(resolve => queueMicrotask(resolve));

describe('DetailLayoutComponent', () => {
  function mount() {
    TestBed.configureTestingModule({ imports: [HostComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(HostComponent);
    document.body.append(fixture.nativeElement);
    fixture.detectChanges();
    return fixture;
  }

  it('shows no panel while closed and the titled panel with body and footer while open', async () => {
    const fixture = mount();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="detail-layout-panel"]')).toBeNull();

    fixture.componentInstance.open.set(true);
    fixture.detectChanges();
    await settle();

    const panel = root.querySelector('[data-testid="detail-layout-panel"]')!;
    expect(panel.getAttribute('aria-label')).toBe('Service account: deploy-bot');
    expect(panel.querySelector('.sidesheet__eyebrow')!.textContent).toBe('Service account');
    expect(panel.querySelector('.sidesheet__title')!.textContent).toBe('deploy-bot');
    expect(panel.querySelector('.detail-layout__body .facts')).toBeTruthy();
    expect(panel.querySelector('.detail-layout__footer .danger')).toBeTruthy();
    fixture.destroy();
  });

  it('moves focus into the panel on open and back to the opener on close', async () => {
    const fixture = mount();
    const root = fixture.nativeElement as HTMLElement;
    const opener = root.querySelector('.opener') as HTMLButtonElement;
    opener.focus();
    opener.click();
    fixture.detectChanges();
    await settle();
    expect(document.activeElement).toBe(root.querySelector('[data-testid="detail-layout-panel"]'));

    (root.querySelector('[data-testid="sidesheet-close"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await settle();

    expect(root.querySelector('[data-testid="detail-layout-panel"]')).toBeNull();
    expect(document.activeElement).toBe(opener);
    fixture.destroy();
  });

  it('registers with the modal stack while open, so Escape closes the detail first', async () => {
    const fixture = mount();
    const stack = TestBed.inject(ModalStackService);
    const before = stack.depth();
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();
    await settle();
    expect(stack.depth()).toBe(before + 1);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    await settle();

    expect(fixture.componentInstance.open()).toBe(false);
    expect(stack.depth()).toBe(before);
    fixture.destroy();
  });
});

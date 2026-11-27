import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PendingButtonDirective } from './pending-button.directive';

@Component({
  imports: [PendingButtonDirective],
  templateUrl: './pending-button.directive.spec.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class PendingButtonHost {
  readonly pending = signal(false);
  readonly disabled = signal(false);
}

describe('PendingButtonDirective', () => {
  let fixture: ComponentFixture<PendingButtonHost>;
  let host: PendingButtonHost;
  let button: HTMLButtonElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [PendingButtonHost] }).compileComponents();
    fixture = TestBed.createComponent(PendingButtonHost);
    host = fixture.componentInstance;
    fixture.detectChanges();
    button = fixture.nativeElement.querySelector('button');
  });

  it('applies disabled, busy and pending-label feedback in the first render', () => {
    host.pending.set(true);
    fixture.detectChanges();

    expect(button.disabled).toBe(true);
    expect(button.classList).toContain('app-pending-button--pending');
    expect(button.getAttribute('aria-busy')).toBe('true');
    expect(button.getAttribute('data-pending-label')).toBe('Saving…');
  });

  it('follows a disabled binding that changed while the work was pending (AGT-3700)', () => {
    // The panel shape: the button binds [disabled]="!canSubmit()" and canSubmit
    // itself includes the pending flag, so the binding flips with the work.
    host.pending.set(true);
    host.disabled.set(true);
    fixture.detectChanges();
    expect(button.disabled).toBe(true);

    host.pending.set(false);
    host.disabled.set(false);
    fixture.detectChanges();

    expect(button.disabled).toBe(false);
  });

  it('restores the button disabled state that preceded pending work', () => {
    host.disabled.set(true);
    fixture.detectChanges();
    host.pending.set(true);
    fixture.detectChanges();
    host.pending.set(false);
    fixture.detectChanges();

    expect(button.disabled).toBe(true);
    expect(button.hasAttribute('aria-busy')).toBe(false);
    expect(button.hasAttribute('data-pending-label')).toBe(false);
  });
});

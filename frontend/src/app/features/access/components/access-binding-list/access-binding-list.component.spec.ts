import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessBindingListComponent } from './access-binding-list.component';

describe('AccessBindingListComponent', () => {
  async function render(kind: string, id: string) {
    const access = fakeAccessService();
    await TestBed.configureTestingModule({
      imports: [AccessBindingListComponent],
      providers: [provideZonelessChangeDetection(), AccessSelectionService, { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessBindingListComponent);
    fixture.componentRef.setInput('principalKind', kind);
    fixture.componentRef.setInput('principalId', id);
    fixture.detectChanges();
    await fixture.whenStable();
    return { access, fixture, element: fixture.nativeElement as HTMLElement };
  }
  const click = (element: HTMLElement, testId: string) => element.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`)!.click();

  it('lists direct roles with their source and group roles without a remove control', async () => {
    const { element } = await render('user', 'usr_dev');
    const direct = element.querySelector('[data-testid="access-binding-rb_dev"]')!;
    const inherited = element.querySelector('[data-testid="access-binding-rb_group"]')!;

    expect(direct.textContent).toContain('Developer');
    expect(direct.textContent).toContain('Source: manual');
    expect(direct.querySelector('[data-testid="access-binding-remove-rb_dev"]')).toBeTruthy();
    expect(inherited.textContent).toContain('Through the group Team Website');
    expect(inherited.querySelector('app-inline-confirm')).toBeNull();
  });

  it('removes a role only after the confirmation in place', async () => {
    const { access, fixture, element } = await render('user', 'usr_dev');
    click(element, 'access-binding-remove-rb_dev');
    fixture.detectChanges();
    expect(access.removeBinding).not.toHaveBeenCalled();
    expect(element.querySelector('[data-testid="access-binding-remove-rb_dev-panel"]')!.textContent).toContain('Dev User loses Developer in Website.');

    click(element, 'access-binding-remove-rb_dev-confirm');
    await fixture.whenStable();
    expect(access.removeBinding).toHaveBeenCalledWith('rb_dev');
  });

  it('adds a role for this principal in the chosen scope', async () => {
    const { access, fixture, element } = await render('group', 'grp_web');
    const select = (testId: string, value: string) => {
      const control = element.querySelector<HTMLSelectElement>(`[data-testid="${testId}"]`)!;
      control.value = value;
      control.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };
    select('access-grant-scope', 'workspace');
    select('access-grant-role', 'viewer');
    await fixture.whenStable();
    click(element, 'access-binding-add');
    await fixture.whenStable();
    expect(access.addBinding).toHaveBeenCalledWith('group', 'grp_web', 'viewer', { kind: 'workspace' });
  });
});

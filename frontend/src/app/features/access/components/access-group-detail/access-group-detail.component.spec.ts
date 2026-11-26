import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessGroupDetailComponent } from './access-group-detail.component';

describe('AccessGroupDetailComponent', () => {
  async function render() {
    const access = fakeAccessService();
    await TestBed.configureTestingModule({
      imports: [AccessGroupDetailComponent],
      providers: [provideZonelessChangeDetection(), AccessSelectionService, { provide: AccessService, useValue: access }],
    }).compileComponents();
    const selection = TestBed.inject(AccessSelectionService);
    selection.open('group', 'grp_web');
    const fixture = TestBed.createComponent(AccessGroupDetailComponent);
    fixture.componentRef.setInput('groupId', 'grp_web');
    fixture.detectChanges();
    await fixture.whenStable();
    return { access, fixture, element: fixture.nativeElement as HTMLElement, selection };
  }
  const click = (element: HTMLElement, selector: string) => element.querySelector<HTMLButtonElement>(selector)!.click();

  it('shows the members and the roles of the group', async () => {
    const { element } = await render();
    expect(element.querySelector('[aria-label="Members"]')!.textContent).toContain('Dev User');
    expect(element.querySelector('[data-testid="access-binding-rb_group"]')!.textContent).toContain('Approver');
  });

  it('adds a member from the people who are not in the group', async () => {
    const { access, element } = await render();
    const select = element.querySelector<HTMLSelectElement>('[data-testid="access-group-add-grp_web"]')!;
    expect([...select.options].map(option => option.value)).toEqual(['', 'usr_owner']);
    select.value = 'usr_owner';
    select.dispatchEvent(new Event('change'));
    expect(access.updateGroupMembers).toHaveBeenCalledWith('grp_web', ['usr_dev', 'usr_owner']);
  });

  it('removes a member', async () => {
    const { access, element } = await render();
    click(element, '[aria-label="Remove Dev User"]');
    expect(access.updateGroupMembers).toHaveBeenCalledWith('grp_web', []);
  });

  it('deletes the group only after the confirmation, then closes the detail', async () => {
    const { access, element, fixture, selection } = await render();
    click(element, '[data-testid="access-group-delete-grp_web"]');
    fixture.detectChanges();
    expect(access.deleteGroup).not.toHaveBeenCalled();
    expect(element.querySelector('[data-testid="access-group-delete-grp_web-panel"]')!.textContent).toContain('lose the roles they hold through it');

    click(element, '[data-testid="access-group-delete-grp_web-confirm"]');
    await fixture.whenStable();
    expect(access.deleteGroup).toHaveBeenCalledWith('grp_web');
    expect(selection.selection()).toBeNull();
  });
});

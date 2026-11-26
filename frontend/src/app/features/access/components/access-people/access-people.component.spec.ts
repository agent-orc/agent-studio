import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { AccessPeopleComponent } from './access-people.component';

describe('AccessPeopleComponent', () => {
  async function render(access = fakeAccessService()) {
    await TestBed.configureTestingModule({
      imports: [AccessPeopleComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        AccessSelectionService, { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessPeopleComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    return { access, fixture, element: fixture.nativeElement as HTMLElement, selection: TestBed.inject(AccessSelectionService) };
  }

  it('shows each person with direct and group roles, the state and the second factor', async () => {
    const { element } = await render();
    const developer = element.querySelector('[data-testid="access-user-dev.user"]')!;
    const chips = [...developer.querySelectorAll('.chip')].map(chip => chip.getAttribute('title'));

    expect(developer.textContent).toContain('Dev User');
    expect(developer.textContent).toContain('dev.user');
    expect(chips).toEqual(['Developer · Website', 'Approver · Website · production']);
    const invited = element.querySelector<HTMLElement>('[data-testid="access-user-status-dev.user"]')!;
    expect([invited.dataset['tone'], invited.dataset['icon'], invited.textContent?.trim()]).toEqual(['info', 'info', 'Invited']);
    expect(element.querySelector('[data-testid="access-user-status-first.owner"]')!.textContent!.trim()).toBe('Active');
    expect(developer.querySelectorAll('.chip')[1].getAttribute('data-emphasis')).toBe('quiet');
    expect(developer.querySelector('td[data-role="meta"]')!.textContent!.trim()).toBe('None');
  });

  it('opens the detail of a person from the row', async () => {
    const { element, fixture, selection } = await render();
    element.querySelector<HTMLButtonElement>('[data-testid="access-user-dev.user"] .grid__open')!.click();
    fixture.detectChanges();

    expect(selection.selection()).toEqual({ mode: 'detail', kind: 'user', id: 'usr_dev' });
    expect(element.querySelector('[data-testid="access-user-dev.user"]')!.getAttribute('data-selected')).toBe('true');
  });

  it('leads Disable from the row menu to the confirmation in the detail and enables a disabled person directly', async () => {
    const overview = accessOverview();
    const access = fakeAccessService({ ...overview, users: [overview.users[0], { ...overview.users[1], disabled: true }] });
    const { fixture, selection } = await render(access);
    const component = fixture.componentInstance;

    component.act('disable', component.rows()[0]);
    expect(selection.selection()).toEqual({ mode: 'detail', kind: 'user', id: 'usr_owner' });
    expect(selection.armed()).toBe('disable');
    expect(access.setUserDisabled).not.toHaveBeenCalled();

    expect(component.rows()[1].menu.at(-1)).toEqual({ kind: 'row', id: 'enable', label: 'Enable' });
    component.act('enable', component.rows()[1]);
    expect(access.setUserDisabled).toHaveBeenCalledWith('usr_dev', false);
  });

  it('says what belongs here when nobody is listed', async () => {
    const { element } = await render(fakeAccessService(accessOverview({ users: [] })));
    expect(element.querySelector('[data-testid="access-users-empty"]')!.textContent).toContain('Everyone with their own sign-in');
  });
});

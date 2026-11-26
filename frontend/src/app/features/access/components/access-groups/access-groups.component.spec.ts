import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { AccessGroupsComponent } from './access-groups.component';

describe('AccessGroupsComponent', () => {
  async function render(access = fakeAccessService()) {
    await TestBed.configureTestingModule({
      imports: [AccessGroupsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        AccessSelectionService, { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessGroupsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    return { access, fixture, element: fixture.nativeElement as HTMLElement, selection: TestBed.inject(AccessSelectionService) };
  }

  it('lists a group with its role, its members and whether it grants anything', async () => {
    const { element } = await render();
    const row = element.querySelector('[data-testid="access-group-grp_web"]')!;

    expect(row.textContent).toContain('Team Website');
    expect(row.textContent).toContain('Website crew');
    expect(row.querySelector('.chip')!.getAttribute('title')).toBe('Approver · Website · production');
    expect(row.querySelector('td[data-role="meta"]')!.textContent!.trim()).toBe('Dev User');
    expect(element.querySelector('[data-testid="access-group-status-grp_web"]')!.textContent!.trim()).toBe('Active');
  });

  it('marks a group without members or without a role as granting nothing', async () => {
    const overview = accessOverview();
    const access = fakeAccessService({ ...overview, groups: [
      { ...overview.groups[0], members: [] },
      { id: 'grp_idle', name: 'Idle', description: null, members: ['usr_dev'], createdAt: '2026-10-20T09:00:00Z' },
    ] });
    const { element } = await render(access);

    expect(element.querySelector('[data-testid="access-group-status-grp_web"]')!.textContent!.trim()).toBe('No members');
    expect(element.querySelector('[data-testid="access-group-status-grp_idle"]')!.textContent!.trim()).toBe('No role');
    expect(element.querySelector('[data-testid="access-group-grp_web"] td[data-role="meta"] .grid__none')).toBeTruthy();
  });

  it('opens the detail from the row and leads Delete to the confirmation there', async () => {
    const { access, element, fixture, selection } = await render();
    element.querySelector<HTMLButtonElement>('[data-testid="access-group-grp_web"] .grid__open')!.click();
    expect(selection.selection()).toEqual({ mode: 'detail', kind: 'group', id: 'grp_web' });

    fixture.componentInstance.act('delete', fixture.componentInstance.rows()[0]);
    expect(selection.armed()).toBe('delete');
    expect(access.deleteGroup).not.toHaveBeenCalled();
  });

  it('says what belongs here when there is no group', async () => {
    const { element } = await render(fakeAccessService(accessOverview({ groups: [] })));
    expect(element.querySelector('[data-testid="access-groups-empty"]')!.textContent).toContain('A group holds a role once for several people');
  });
});

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessGroupsComponent } from './access-groups.component';

describe('AccessGroupsComponent', () => {
  let access: ReturnType<typeof fakeAccessService>;

  beforeEach(async () => {
    access = fakeAccessService();
    await TestBed.configureTestingModule({
      imports: [AccessGroupsComponent],
      providers: [provideZonelessChangeDetection(), { provide: AccessService, useValue: access }],
    }).compileComponents();
  });

  it('shows members and binding counts and edits membership', async () => {
    const fixture = TestBed.createComponent(AccessGroupsComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const group = element.querySelector('[data-testid="access-group-grp_web"]')?.textContent ?? '';
    expect(group).toContain('Dev User');
    expect(group).toContain('1 binding');

    const add = element.querySelector('[data-testid="access-group-add-grp_web"]') as HTMLSelectElement;
    add.value = 'usr_owner';
    add.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(access.updateGroupMembers).toHaveBeenCalledWith('grp_web', ['usr_dev', 'usr_owner']);

    (element.querySelector('[aria-label="Remove Dev User"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(access.updateGroupMembers).toHaveBeenLastCalledWith('grp_web', []);
  });

  it('creates a group', async () => {
    const fixture = TestBed.createComponent(AccessGroupsComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const name = element.querySelector('[data-testid="access-group-name"]') as HTMLInputElement;
    name.value = 'Reviewers';
    name.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-group-create"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(access.createGroup).toHaveBeenCalledWith('Reviewers', '', []);
  });
});

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessGroupFormComponent } from './access-group-form.component';

describe('AccessGroupFormComponent', () => {
  it('creates a group from the trimmed name and opens it', async () => {
    const access = fakeAccessService();
    access.createGroup.mockResolvedValue({ id: 'grp_new' });
    await TestBed.configureTestingModule({
      imports: [AccessGroupFormComponent],
      providers: [provideZonelessChangeDetection(), AccessSelectionService, { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessGroupFormComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const create = element.querySelector<HTMLButtonElement>('[data-testid="access-group-create"]')!;
    expect(create.disabled).toBe(true);

    const name = element.querySelector<HTMLInputElement>('[data-testid="access-group-name"]')!;
    name.value = '  Approvers ';
    name.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    create.click();
    await fixture.whenStable();

    expect(access.createGroup).toHaveBeenCalledWith('Approvers', '', []);
    expect(TestBed.inject(AccessSelectionService).selection()).toEqual({ mode: 'detail', kind: 'group', id: 'grp_new' });
  });
});

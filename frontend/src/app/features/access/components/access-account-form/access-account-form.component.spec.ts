import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessAccountFormComponent } from './access-account-form.component';

describe('AccessAccountFormComponent', () => {
  it('creates a service account with its first role and opens it', async () => {
    const access = fakeAccessService();
    access.createServiceAccount.mockResolvedValue({ id: 'sa_new' });
    await TestBed.configureTestingModule({
      imports: [AccessAccountFormComponent],
      providers: [provideZonelessChangeDetection(), AccessSelectionService, { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessAccountFormComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const set = (testId: string, value: string, event: string) => {
      const control = element.querySelector<HTMLInputElement | HTMLSelectElement>(`[data-testid="${testId}"]`)!;
      control.value = value;
      control.dispatchEvent(new Event(event));
      fixture.detectChanges();
    };
    set('access-service-account-name', ' deploy-bot ', 'input');
    set('access-grant-scope', 'workspace', 'change');
    set('access-grant-role', 'viewer', 'change');
    await fixture.whenStable();
    element.querySelector<HTMLButtonElement>('[data-testid="access-service-account-create"]')!.click();
    await fixture.whenStable();

    expect(access.createServiceAccount).toHaveBeenCalledWith('deploy-bot', '', [{ role: 'viewer', scope: { kind: 'workspace' } }]);
    expect(TestBed.inject(AccessSelectionService).selection()).toEqual({ mode: 'detail', kind: 'service-account', id: 'sa_new' });
  });
});

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessBindingFormComponent } from './access-binding-form.component';

describe('AccessBindingFormComponent', () => {
  it('adds a binding for the chosen principal and grant, then opens the principal', async () => {
    const access = fakeAccessService();
    await TestBed.configureTestingModule({
      imports: [AccessBindingFormComponent],
      providers: [provideZonelessChangeDetection(), AccessSelectionService, { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessBindingFormComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const principal = element.querySelector('[data-testid="access-binding-principal"]') as HTMLSelectElement;
    expect([...principal.options].map(option => option.textContent!.trim()))
      .toEqual(['User: First Owner (first.owner)', 'User: Dev User (dev.user)', 'Group: Team Website']);
    principal.value = 'group:grp_web';
    principal.dispatchEvent(new Event('change'));
    const project = element.querySelector('[data-testid="access-grant-project"]') as HTMLSelectElement;
    project.value = 'PROJ-002';
    project.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-binding-add"]') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(access.addBinding).toHaveBeenCalledWith('group', 'grp_web', 'admin', { kind: 'project', project: 'PROJ-002' });
    expect(TestBed.inject(AccessSelectionService).selection()).toEqual({ mode: 'detail', kind: 'group', id: 'grp_web' });
  });
});

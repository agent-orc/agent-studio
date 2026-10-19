import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { AccessService } from '../../services/access.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { AccessBindingsComponent } from './access-bindings.component';

describe('AccessBindingsComponent', () => {
  let access: ReturnType<typeof fakeAccessService>;

  beforeEach(async () => {
    access = fakeAccessService();
    await TestBed.configureTestingModule({
      imports: [AccessBindingsComponent],
      providers: [provideZonelessChangeDetection(), { provide: AccessService, useValue: access }],
    }).compileComponents();
  });

  it('lists bindings per scope with principal and role names', () => {
    const fixture = TestBed.createComponent(AccessBindingsComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const scopes = [...element.querySelectorAll('[data-testid^="access-scope-"]')].map(node => node.getAttribute('data-testid'));
    expect(scopes).toEqual(['access-scope-workspace', 'access-scope-project:proj-001', 'access-scope-environment:proj-001/production']);
    expect(element.querySelector('[data-testid="access-binding-rb_group"]')?.textContent).toContain('Team Website');
    expect(element.querySelector('[data-testid="access-binding-rb_dev"]')?.textContent).toContain('Developer');
    expect(element.querySelector('[data-testid="access-scope-environment:proj-001/production"] h4')?.textContent).toContain('Website · production');
  });

  it('adds a binding for the chosen principal and grant', async () => {
    const fixture = TestBed.createComponent(AccessBindingsComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const principal = element.querySelector('[data-testid="access-binding-principal"]') as HTMLSelectElement;
    principal.value = 'group:grp_web';
    principal.dispatchEvent(new Event('change'));
    const project = element.querySelector('[data-testid="access-grant-project"]') as HTMLSelectElement;
    project.value = 'PROJ-002';
    project.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    (element.querySelector('[data-testid="access-binding-add"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(access.addBinding).toHaveBeenCalledWith('group', 'grp_web', 'admin', { kind: 'project', project: 'PROJ-002' });
  });

  it('removes a binding', async () => {
    const fixture = TestBed.createComponent(AccessBindingsComponent);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="access-binding-rb_dev"] button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(access.removeBinding).toHaveBeenCalledWith('rb_dev');
  });
});

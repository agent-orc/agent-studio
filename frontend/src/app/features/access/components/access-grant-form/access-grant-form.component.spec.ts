import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { IamGrant } from '../../models/access.model';
import { ACCESS_PROJECTS, ACCESS_ROLES } from '../../testing/access-fixtures';
import { AccessGrantFormComponent } from './access-grant-form.component';

describe('AccessGrantFormComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AccessGrantFormComponent], providers: [provideZonelessChangeDetection()] }).compileComponents();
  });

  function create(principalKind: 'user' | 'service-account' = 'user') {
    const fixture = TestBed.createComponent(AccessGrantFormComponent);
    fixture.componentRef.setInput('roles', ACCESS_ROLES);
    fixture.componentRef.setInput('projects', ACCESS_PROJECTS);
    fixture.componentRef.setInput('principalKind', principalKind);
    const grants: (IamGrant | null)[] = [];
    fixture.componentInstance.grantChange.subscribe(grant => grants.push(grant));
    fixture.detectChanges();
    return { fixture, grants, element: fixture.nativeElement as HTMLElement };
  }

  function choose(element: HTMLElement, testId: string, value: string): void {
    const select = element.querySelector(`[data-testid="${testId}"]`) as HTMLSelectElement;
    select.value = value;
    select.dispatchEvent(new Event('change'));
  }

  function roleOptions(element: HTMLElement): string[] {
    return [...element.querySelectorAll('[data-testid="access-grant-role"] option')].map(option => option.textContent?.trim() ?? '');
  }

  it('emits a project grant only once a project is chosen', () => {
    const { fixture, grants, element } = create();
    expect(grants.at(-1)).toBeNull();
    choose(element, 'access-grant-project', 'PROJ-002');
    choose(element, 'access-grant-role', 'developer');
    fixture.detectChanges();
    expect(grants.at(-1)).toEqual({ role: 'developer', scope: { kind: 'project', project: 'PROJ-002' } });
    expect(element.querySelector('[data-testid="access-grant-role-description"]')?.textContent).toContain('Works on cards.');
  });

  it('switches roles to the scope kind and keeps an environment name', () => {
    const { fixture, grants, element } = create();
    choose(element, 'access-grant-scope', 'workspace');
    fixture.detectChanges();
    expect(roleOptions(element)).toEqual(['Owner', 'Admin', 'Developer', 'Viewer', 'Approver']);
    // The chosen role stays while the new scope kind still allows it.
    expect(grants.at(-1)).toEqual({ role: 'admin', scope: { kind: 'workspace' } });

    choose(element, 'access-grant-scope', 'environment');
    fixture.detectChanges();
    choose(element, 'access-grant-project', 'PROJ-001');
    choose(element, 'access-grant-role', 'approver');
    fixture.detectChanges();
    expect(grants.at(-1)).toEqual({ role: 'approver', scope: { kind: 'environment', project: 'PROJ-001', environment: 'production' } });
  });

  it('never offers human-only roles to a service account', () => {
    const { fixture, element } = create('service-account');
    choose(element, 'access-grant-scope', 'workspace');
    fixture.detectChanges();
    expect(roleOptions(element)).toEqual(['Admin', 'Developer', 'Viewer']);
  });
});

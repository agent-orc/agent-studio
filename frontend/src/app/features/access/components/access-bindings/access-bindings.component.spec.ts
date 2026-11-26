import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { accessOverview, fakeAccessService } from '../../testing/access-fixtures';
import { ACCOUNTS, ACCOUNT_BINDINGS } from '../../testing/access-account-fixtures';
import { AccessBindingsComponent } from './access-bindings.component';

describe('AccessBindingsComponent', () => {
  async function render(access = fakeAccessService()) {
    await TestBed.configureTestingModule({
      imports: [AccessBindingsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        AccessSelectionService, { provide: AccessService, useValue: access }],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessBindingsComponent);
    fixture.detectChanges();
    return { access, fixture, element: fixture.nativeElement as HTMLElement, selection: TestBed.inject(AccessSelectionService) };
  }

  it('lists bindings per scope with principal, kind, role and source', async () => {
    const { element } = await render();
    const scopes = [...element.querySelectorAll('section[data-testid^="access-scope-"]')].map(node => node.getAttribute('data-testid'));

    expect(scopes).toEqual(['access-scope-workspace', 'access-scope-project:proj-001', 'access-scope-environment:proj-001/production']);
    const group = element.querySelector('[data-testid="access-binding-rb_group"]')!;
    expect(group.textContent).toContain('Team Website');
    expect(group.textContent).toContain('Group');
    expect(element.querySelector('[data-testid="access-binding-rb_dev"] .chip')!.getAttribute('title')).toBe('Developer');
    expect(element.querySelector('[data-testid="access-binding-rb_dev"] td[data-role="meta"]')!.textContent!.trim()).toBe('manual');
    expect(element.querySelector('[data-testid="access-binding-rb_owner"] .chip')!.getAttribute('data-emphasis')).toBe('strong');
    expect(element.querySelector('[data-testid="access-scope-environment:proj-001/production"] h4')!.textContent).toContain('Website · production');
  });

  it('opens the principal of a binding from its row', async () => {
    const { element, selection } = await render();
    element.querySelector<HTMLButtonElement>('[data-testid="access-binding-rb_group"] .grid__open')!.click();
    expect(selection.selection()).toEqual({ mode: 'detail', kind: 'group', id: 'grp_web' });
  });

  it('leads Remove role to the confirmation in the detail of the principal, without removing yet', async () => {
    const { access, fixture, selection } = await render();
    const row = fixture.componentInstance.scopes()[1].rows[0];
    fixture.componentInstance.open(row, true);

    expect(selection.selection()).toEqual({ mode: 'detail', kind: 'user', id: 'usr_dev' });
    expect(selection.armed()).toBe('remove-binding:rb_dev');
    expect(access.removeBinding).not.toHaveBeenCalled();
  });

  it('opens an API token in its own detail kind', async () => {
    const { fixture, selection } = await render(fakeAccessService(accessOverview({ serviceAccounts: ACCOUNTS, bindings: ACCOUNT_BINDINGS })));
    const rows = fixture.componentInstance.scopes().flatMap(scope => scope.rows);
    const token = rows.find(row => row.id === 'rb_build')!;
    expect(token.sub).toBe('API token');
    fixture.componentInstance.open(token);
    expect(selection.selection()).toEqual({ mode: 'detail', kind: 'api-token', id: 'sa_tok_build' });
    expect(rows.find(row => row.id === 'rb_sa')!.detail).toBe('service-account');
  });

  it('says what belongs here when there is no binding', async () => {
    const { element } = await render(fakeAccessService(accessOverview({ bindings: [] })));
    expect(element.querySelector('[data-testid="access-bindings-empty"]')!.textContent).toContain('under the scope it applies to');
  });
});

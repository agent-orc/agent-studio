import { describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { RegistryProjectSummary } from '../../../models/task.model';
import { ServerCheckoutStore } from '../server-checkout.store';
import { serverCheckoutView, type ServerCheckoutStatus } from '../server-checkout.model';
import { urlProject } from '../testing/server-checkout.fixtures';
import { ServerCheckoutNoticeComponent } from './server-checkout-notice.component';
import { ServerCheckoutBoardNoticeComponent } from '../server-checkout-board-notice/server-checkout-board-notice.component';

function setup(projects: RegistryProjectSummary[], status: ServerCheckoutStatus | null = null) {
  const statusSignal = signal(status);
  const store = {
    start: vi.fn(), load: vi.fn(),
    projectFor: (reference: { id?: string | null; name?: string | null }) => projects.find(project =>
      project.id === reference.id || project.displayName === reference.name) ?? null,
    viewFor: (project: RegistryProjectSummary | null) => project ? serverCheckoutView(project, statusSignal()) : null,
    noticeProjects: () => projects.filter(project => serverCheckoutView(project, statusSignal()) !== null),
  };
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(),
    { provide: ServerCheckoutStore, useValue: store }] });
  return { store, statusSignal };
}

describe('ServerCheckoutNoticeComponent (AGT-3809)', () => {
  it('offers the action for a URL project without a checkout and starts it on click', () => {
    const { store } = setup([urlProject()]);
    const fixture = TestBed.createComponent(ServerCheckoutNoticeComponent);
    fixture.componentRef.setInput('projectName', 'Library');
    fixture.componentRef.setInput('loadOnOpen', true);
    fixture.detectChanges();
    const notice = fixture.nativeElement.querySelector('[data-testid="server-checkout-notice"]') as HTMLElement;
    expect(notice.dataset['state']).toBe('missing');
    expect(notice.textContent).toContain('No server checkout');
    expect(store.load).toHaveBeenCalledWith('PROJ-2');
    const outerClick = vi.fn();
    fixture.nativeElement.addEventListener('click', outerClick);
    (fixture.nativeElement.querySelector('[data-testid="server-checkout-create"]') as HTMLButtonElement).click();
    expect(store.start).toHaveBeenCalledWith('PROJ-2');
    expect(outerClick).not.toHaveBeenCalled();
  });

  it('shows the resulting path in full and hides a finished checkout on a Ready card', () => {
    const { statusSignal } = setup([urlProject()], { projectId: 'PROJ-2', state: 'ready', step: 'register',
      repositoryUrl: null, repositoryPath: '/srv/checkouts/PROJ-2', branch: 'develop', error: null });
    const fixture = TestBed.createComponent(ServerCheckoutNoticeComponent);
    fixture.componentRef.setInput('projectId', 'PROJ-2');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="server-checkout-path"]')?.textContent).toBe('/srv/checkouts/PROJ-2');
    expect(fixture.nativeElement.querySelector('[data-testid="server-checkout-create"]')).toBeNull();
    fixture.componentRef.setInput('compact', true);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="server-checkout-notice"]')).toBeNull();
    statusSignal.set(null);
  });

  it('renders nothing for a project that already has a checkout', () => {
    setup([urlProject({ repositoryPath: '/srv/library' })]);
    const fixture = TestBed.createComponent(ServerCheckoutNoticeComponent);
    fixture.componentRef.setInput('projectId', 'PROJ-2');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="server-checkout-notice"]')).toBeNull();
  });
});

describe('ServerCheckoutBoardNoticeComponent (AGT-3809)', () => {
  it('shows one notice per affected project in the board scope', () => {
    setup([urlProject(), urlProject({ id: 'PROJ-3', displayName: 'Tools', repositoryUrl: 'https://github.com/example/tools.git' })]);
    const fixture = TestBed.createComponent(ServerCheckoutBoardNoticeComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="server-checkout-notice"]').length).toBe(2);
    fixture.componentRef.setInput('projectScope', 'tools');
    fixture.detectChanges();
    const notices = fixture.nativeElement.querySelectorAll('[data-testid="server-checkout-notice"]');
    expect(notices.length).toBe(1);
    expect(notices[0].textContent).toContain('https://github.com/example/tools.git');
  });
});

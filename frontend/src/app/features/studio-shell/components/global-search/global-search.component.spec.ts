import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { TaskInfo } from '../../../../models/task.model';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { GlobalSearchComponent } from './global-search.component';
import type { GlobalSearchItem } from './global-search.service';

const DOSSIER: GlobalSearchItem = {
  domain: 'dossiers', projectName: 'Agent Studio', projectColor: '#569cd6',
  title: 'Orchestrator watcher', subtitle: 'decision-pending · decision-ready',
  dossierKey: 'AGT-W15', dossierId: 'orchestrator-waechter',
  summary: 'Watch the runner loop and surface stalled runs.',
};

describe('GlobalSearchComponent', () => {
  let fixture: ComponentFixture<GlobalSearchComponent>;
  let component: GlobalSearchComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [GlobalSearchComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(GlobalSearchComponent);
    component = fixture.componentInstance;
  });

  it('opens with Ctrl+K and closes with Escape', () => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }));
    expect(component.open()).toBe(true);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.open()).toBe(false);
  });

  it('ranks an exact task key before a title match from in-memory board state', () => {
    fixture.componentRef.setInput('tasks', [
      { taskKey: 'a', key: 'AGT-20', title: 'AGT-2034 follow-up', projectName: 'P', state: '2-ready', id: 'a' },
      { taskKey: 'b', key: 'AGT-2034', title: 'Global search', projectName: 'P', state: '3-progress', id: 'b' },
    ] as TaskInfo[]);
    component.query.set('AGT-2034');

    expect(component.taskResults().map(x => x.taskKey)).toEqual(['b', 'a']);
  });

  it('asks the backend for the dossier domain alongside tasks, commits, and files', () => {
    vi.useFakeTimers();
    try {
      component.onQuery('AGT-W15');
      vi.advanceTimersByTime(120);
      const request = TestBed.inject(HttpTestingController)
        .expectOne(candidate => candidate.url === '/api/search');
      expect(request.request.params.get('domains')).toBe('tasks,commits,files,dossiers');
      request.flush({
        query: 'AGT-W15', tasks: [], commits: [], files: [], dossiers: [DOSSIER],
        errors: {}, durationMs: 4,
      });
      expect(component.remote().dossiers).toEqual([DOSSIER]);
    } finally {
      vi.useRealTimers();
    }
  });

  it('renders a Dossiers group with the key badge, status and phase chip, and the summary', () => {
    component.open.set(true);
    component.query.set('AGT-W15');
    component.remote.set({ commits: [], files: [], dossiers: [DOSSIER], errors: {} });
    fixture.detectChanges();

    const group: HTMLElement = fixture.nativeElement.querySelector('[data-testid="global-search-group-dossiers"]');
    expect(group.querySelector('h2')?.textContent).toBe('Dossiers');
    expect(group.querySelector('strong')?.textContent).toBe('Orchestrator watcher');
    expect(group.querySelector('small')?.textContent)
      .toBe('Agent Studio · Watch the runner loop and surface stalled runs.');
    expect([...group.querySelectorAll('.search-badge')].map(badge => badge.textContent))
      .toEqual(['AGT-W15', 'decision-pending · decision-ready']);
  });

  it('opens the dossier viewer tab for the selected dossier and closes the palette', () => {
    const open = vi.spyOn(TestBed.inject(StudioTabStateService), 'open').mockImplementation(() => undefined);
    component.open.set(true);

    component.choose(DOSSIER);

    expect(open).toHaveBeenCalledWith({
      kind: 'workbench', projectName: 'Agent Studio',
      workbenchId: 'orchestrator-waechter', title: 'Orchestrator watcher', key: 'AGT-W15',
    });
    expect(component.open()).toBe(false);
  });

  it('keeps arrow navigation continuous across the four result groups', () => {
    fixture.componentRef.setInput('tasks', [
      { taskKey: 'a', key: 'AGT-2034', title: 'Global search', projectName: 'P', state: '2-ready', id: 'a' },
    ] as TaskInfo[]);
    component.open.set(true);
    component.query.set('AGT');
    component.remote.set({
      commits: [{ domain: 'commits', projectName: 'P', projectColor: '#fff', title: 'AGT fix', subtitle: 'abc1234', sha: 'abc1234' }],
      files: [], dossiers: [DOSSIER], errors: {},
    });

    expect(component.flatResults().map(item => item.domain)).toEqual(['tasks', 'dossiers', 'commits']);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown' }));
    expect(component.activeIndex()).toBe(1);
  });
});

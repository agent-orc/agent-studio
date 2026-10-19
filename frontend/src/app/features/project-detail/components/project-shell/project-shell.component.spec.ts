import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectShellComponent } from './project-shell.component';
import type { ProjectRailKey } from './project-shell.config';

/**
 * Render-path coverage for the Deck nav IA (ASS-1711): collapsible
 * main segments, the tree-expandable Settings parent, the "Agent Docs"
 * rename, the Context main point, and the shared tree-row icon rail.
 */
function mount(activeRail: ProjectRailKey = 'overview') {
  TestBed.configureTestingModule({
    imports: [ProjectShellComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  const fixture = TestBed.createComponent(ProjectShellComponent);
  fixture.componentRef.setInput('projectName', 'Demo Project');
  fixture.componentRef.setInput('activeRail', activeRail);
  fixture.detectChanges();
  return fixture;
}

function railEl(host: HTMLElement, key: string): HTMLElement | null {
  return host.querySelector<HTMLElement>(`[data-testid="project-shell-rail-${key}"]`);
}

function projectShellStorageKey(projectName = 'Demo Project'): string {
  return `atp.projectShell.v1.${encodeURIComponent(projectName)}`;
}

function clearProjectShellStorage(): void {
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith('atp.projectShell.v1.')) localStorage.removeItem(key);
  }
}

function resizeEvent(
  clientX: number,
  target: Pick<HTMLElement, 'setPointerCapture' | 'releasePointerCapture'>,
): PointerEvent {
  return {
    clientX,
    currentTarget: target,
    pointerId: 7,
    preventDefault: vi.fn(),
  } as unknown as PointerEvent;
}

describe('ProjectShellComponent rail IA', () => {
  beforeEach(() => {
    clearProjectShellStorage();
  });

  it('renders one balanced Deck header with icon actions and legacy selector', () => {
    const host = mount().nativeElement as HTMLElement;
    const back = host.querySelector<HTMLElement>('[data-testid="project-shell-back"]')!;
    const feed = host.querySelector<HTMLElement>('[data-testid="project-shell-open-feed"]')!;

    expect(host.querySelector('[data-testid="project-shell-title"]')!.textContent).toContain('Demo Project');
    expect(host.querySelector('[data-testid="deck-sidebar-header"]')!.textContent).toContain('Deck');
    expect(host.querySelector('[data-testid="project-shell-sidebar-header"]')!.textContent).toContain('Deck');
    expect(back.getAttribute('aria-label')).toBe('Collapse project navigation');
    expect(feed.getAttribute('aria-label')).toBe('Open project activity feed');
    expect(back.querySelector('app-studio-icon')).toBeTruthy();
    expect(feed.querySelector('app-studio-icon')).toBeTruthy();
  });

  it('collapses the project navigation into an icon rail and expands through the splitter', () => {
    const fixture = mount('security');
    const host = fixture.nativeElement as HTMLElement;

    host.querySelector<HTMLElement>('[data-testid="project-shell-back"]')!.click();
    fixture.detectChanges();

    expect(host.querySelector<HTMLElement>('[data-testid="project-shell-rail"]')?.getAttribute('data-collapsed')).toBe('true');
    expect(JSON.parse(localStorage.getItem(projectShellStorageKey()) ?? '{}').railCollapsed).toBe(true);
    expect(host.querySelector('[data-testid="project-shell-sidebar-header"]')).toBeNull();
    expect(host.querySelector('[data-testid="project-shell-expand-nav"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="project-shell-mini-rail-security"]')?.getAttribute('aria-current')).toBe('page');
    expect(host.querySelector('[data-testid="project-shell-panel-security"]')).toBeTruthy();

    host.querySelector<HTMLElement>('[data-testid="project-shell-splitter"]')!.click();
    fixture.detectChanges();

    expect(host.querySelector<HTMLElement>('[data-testid="project-shell-rail"]')?.getAttribute('data-collapsed')).toBe('false');
    expect(JSON.parse(localStorage.getItem(projectShellStorageKey()) ?? '{}').railCollapsed).toBe(false);
    expect(host.querySelector('[data-testid="project-shell-sidebar-header"]')).toBeTruthy();
  });

  it('restores collapsed project navigation state after a reload', () => {
    localStorage.setItem(projectShellStorageKey(), JSON.stringify({
      railCollapsed: true,
      railWidth: 320,
      collapsedGroups: ['insight'],
      expandedParents: ['settings'],
    }));

    const fixture = mount('security');
    const component = fixture.componentInstance;
    const host = fixture.nativeElement as HTMLElement;

    expect(component.railCollapsed()).toBe(true);
    expect(component.railWidth()).toBe(320);
    expect(component.isGroupCollapsed('insight')).toBe(true);
    expect(component.isExpanded('settings')).toBe(true);
    expect(host.querySelector('[data-testid="project-shell-sidebar-header"]')).toBeNull();
  });

  it('resizes the project navigation by dragging the splitter and collapses below threshold', () => {
    const fixture = mount();
    const component = fixture.componentInstance;
    const target = {
      setPointerCapture: vi.fn(),
      releasePointerCapture: vi.fn(),
    } as unknown as HTMLElement;

    component.startRailResize(resizeEvent(240, target));
    component.resizeRail({ clientX: 320, pointerId: 7 } as PointerEvent);

    expect(target.setPointerCapture).toHaveBeenCalledWith(7);
    expect(component.railCollapsed()).toBe(false);
    expect(component.railWidth()).toBe(320);

    component.resizeRail({ clientX: 80, pointerId: 7 } as PointerEvent);
    component.finishRailResize({ currentTarget: target, pointerId: 7 } as unknown as PointerEvent);

    expect(component.railCollapsed()).toBe(true);
    expect(target.releasePointerCapture).toHaveBeenCalledWith(7);
  });

  it('supports keyboard resizing and collapse on the splitter', () => {
    const fixture = mount();
    const component = fixture.componentInstance;
    const preventDefault = vi.fn();

    component.onSplitterKeydown({ key: 'ArrowRight', preventDefault } as unknown as KeyboardEvent);
    expect(component.railWidth()).toBe(256);

    component.onSplitterKeydown({ key: 'Enter', preventDefault } as unknown as KeyboardEvent);
    expect(component.railCollapsed()).toBe(true);

    component.onSplitterKeydown({ key: 'ArrowRight', preventDefault } as unknown as KeyboardEvent);
    expect(component.railCollapsed()).toBe(false);
    expect(component.railWidth()).toBeGreaterThanOrEqual(component.minRailWidth);
  });

  it('renders the four collapsible main segments', () => {
    const host = mount().nativeElement as HTMLElement;
    for (const id of ['insight', 'quality', 'context', 'config']) {
      const header = host.querySelector(`[data-testid="project-shell-group-${id}"]`);
      expect(header, `segment header ${id}`).toBeTruthy();
      expect(header!.getAttribute('aria-expanded')).toBe('true');
    }
    expect(host.querySelector('[data-testid="project-shell-group-operations"]')).toBeNull();
  });

  it('keeps Token Usage inside Insight and removes the Activity rail', () => {
    const fixture = mount('security');
    const host = fixture.nativeElement as HTMLElement;
    const insightHeader = host.querySelector<HTMLElement>('[data-testid="project-shell-group-insight"]')!;

    expect(railEl(host, 'token-usage')).toBeTruthy();
    expect(railEl(host, 'activity')).toBeNull();

    insightHeader.click();
    fixture.detectChanges();

    expect(railEl(host, 'token-usage')).toBeNull();
  });

  it('collapsing a non-active segment hides its items, expanding shows them again', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;
    const qualityHeader = host.querySelector<HTMLElement>('[data-testid="project-shell-group-quality"]')!;

    expect(railEl(host, 'security')).toBeTruthy();
    qualityHeader.click();
    fixture.detectChanges();
    expect(qualityHeader.getAttribute('aria-expanded')).toBe('false');
    expect(railEl(host, 'security')).toBeNull();

    qualityHeader.click();
    fixture.detectChanges();
    expect(railEl(host, 'security')).toBeTruthy();
  });

  it('renders Context as its own collapsible main segment', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;

    const contextHeader = host.querySelector<HTMLElement>('[data-testid="project-shell-group-context"]')!;
    expect(contextHeader.textContent).toContain('Context');
    expect(contextHeader.getAttribute('aria-expanded')).toBe('true');
    expect(railEl(host, 'steering-docs')).toBeNull();

    // Context rows are visible by default and are top-level rows.
    expect(railEl(host, 'architecture')).toBeTruthy();
    expect(railEl(host, 'wiki')).toBeTruthy();
    expect(railEl(host, 'wiki')!.textContent).toContain('Wiki');
    expect(railEl(host, 'steering')).toBeTruthy();
    expect(railEl(host, 'prompts')).toBeTruthy();

    // Collapsing the main segment hides its rows.
    contextHeader.click();
    fixture.detectChanges();
    expect(railEl(host, 'architecture')).toBeNull();
    expect(railEl(host, 'wiki')).toBeNull();
    expect(railEl(host, 'steering')).toBeNull();
    expect(railEl(host, 'prompts')).toBeNull();
    expect(railEl(host, 'steering-docs')).toBeNull();
  });

  it('collapses Context without emitting a rail change', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;
    let emitted: ProjectRailKey | null = null;
    fixture.componentInstance.railChange.subscribe(k => (emitted = k));

    host.querySelector<HTMLElement>('[data-testid="project-shell-group-context"]')!.click();
    fixture.detectChanges();

    expect(emitted).toBeNull();
    expect(railEl(host, 'architecture')).toBeNull();
  });

  it('labels the agent-read leaf "Agent Docs" and keeps the steering key', () => {
    const host = mount().nativeElement as HTMLElement;
    const leaf = railEl(host, 'steering')!;
    expect(leaf.textContent).toContain('Agent Docs');
    expect(leaf.textContent).not.toContain('Context');
  });

  it('lists Prompts inside Context without a Runtime Prompts placeholder', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;
    const prompts = railEl(host, 'prompts')!;
    expect(prompts.textContent).toContain('Prompts');
    expect(railEl(host, 'runtime-prompts')).toBeNull();
    expect(host.querySelector('[data-testid="project-shell-twisty-prompts"]')).toBeNull();

    host.querySelector<HTMLElement>('[data-testid="project-shell-group-context"]')!.click();
    fixture.detectChanges();
    expect(railEl(host, 'prompts')).toBeNull();
  });

  it('opens Context when an active rail was hidden by persisted state', () => {
    localStorage.setItem(projectShellStorageKey(), JSON.stringify({
      collapsedGroups: ['context'],
    }));

    const fixture = mount('prompts');
    const host = fixture.nativeElement as HTMLElement;

    expect(fixture.componentInstance.isGroupCollapsed('context')).toBe(false);
    expect(railEl(host, 'prompts')).toBeTruthy();
    expect(railEl(host, 'prompts')?.getAttribute('aria-current')).toBe('page');
  });

  it('expands Settings to its grouped sub-pages', () => {
    const host = mount().nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-shell-twisty-settings"]')).toBeTruthy();
    expect(railEl(host, 'settings-defaults')!.textContent).toContain('Workspace Defaults');
    expect(railEl(host, 'settings-overrides')!.textContent).toContain('Project Overrides');
  });

  it('renders rail rows through the shared icon + text tree-row control', () => {
    const host = mount().nativeElement as HTMLElement;
    expect(railEl(host, 'overview')?.querySelector('app-studio-icon')).toBeTruthy();
    expect(railEl(host, 'security')?.querySelector('app-studio-icon')).toBeTruthy();
    expect(railEl(host, 'architecture')?.querySelector('app-studio-icon')).toBeTruthy();
    expect(railEl(host, 'overview')?.querySelector('.tree-row__chev--placeholder')).toBeTruthy();
  });

  it('keeps Deployment, Pipeline, and Observability outside the Deck groups', () => {
    const host = mount().nativeElement as HTMLElement;
    for (const key of ['deployment', 'pipeline', 'observability']) {
      expect(railEl(host, key), key).toBeNull();
    }
    expect(railEl(host, 'workflow')).toBeTruthy();
    expect(railEl(host, 'prompts')).toBeTruthy();
  });

  it('clicking a leaf rail emits its key; re-clicking the active rail is a no-op', () => {
    const fixture = mount('overview');
    const host = fixture.nativeElement as HTMLElement;
    const emitted: ProjectRailKey[] = [];
    fixture.componentInstance.railChange.subscribe(k => emitted.push(k));

    railEl(host, 'security')!.click();
    railEl(host, 'overview')!.click(); // already active → no emit
    fixture.detectChanges();

    expect(emitted).toEqual(['security']);
  });
});

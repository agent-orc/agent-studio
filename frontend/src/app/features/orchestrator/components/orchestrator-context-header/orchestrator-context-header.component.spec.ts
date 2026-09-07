import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import {
  OrchestratorContextHeaderComponent,
  formatElapsed,
} from './orchestrator-context-header.component';

/**
 * Render-path coverage for the orchestrator "where am I" header. Seeds the
 * inputs via setInput and asserts against the projected DOM so the
 * project / task / lane / live-run contract is pinned end-to-end.
 */
describe('OrchestratorContextHeaderComponent', () => {
  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorContextHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    return TestBed.createComponent(OrchestratorContextHeaderComponent);
  }

  function text(fixture: Awaited<ReturnType<typeof makeFixture>>, testid: string): string | null {
    const el = fixture.nativeElement.querySelector(`[data-testid="${testid}"]`);
    return el ? (el.textContent ?? '').trim() : null;
  }

  it('renders nothing until a project is in scope', async () => {
    const fixture = await makeFixture();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-header"]')).toBeNull();
  });

  it('renders project + Board scope when no task is open', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('project', 'demo-project');
    fixture.detectChanges();
    expect(text(fixture, 'orch-context-project')).toContain('demo-project');
    expect(text(fixture, 'orch-context-board')).toBe('Board');
    // No task, no lane, no run -> the meta row is absent.
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-lane"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-run"]')).toBeNull();
    const header = fixture.nativeElement.querySelector('[data-testid="orch-context-header"]');
    expect(header.getAttribute('data-scope')).toBe('board');
  });

  it('renders task key + title + lane label when a task is in scope', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('project', 'demo-project');
    fixture.componentRef.setInput('taskTitle', 'Add orchestrator header');
    fixture.componentRef.setInput('taskKey', 'AGT-1916');
    fixture.componentRef.setInput('taskState', '3-progress');
    fixture.detectChanges();

    const task = text(fixture, 'orch-context-task');
    expect(task).toContain('AGT-1916');
    expect(task).toContain('Add orchestrator header');
    // The lane reads with its shared display name, not a slug fragment.
    expect(text(fixture, 'orch-context-lane')).toBe('In Progress');
    const header = fixture.nativeElement.querySelector('[data-testid="orch-context-header"]');
    expect(header.getAttribute('data-scope')).toBe('task');
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-board"]')).toBeNull();
  });

  it('maps lane keys to a coarse tone', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    fixture.componentRef.setInput('project', 'p');
    fixture.componentRef.setInput('taskState', '3-progress');
    expect(c.laneTone()).toBe('progress');
    fixture.componentRef.setInput('taskState', '5-human-review');
    expect(c.laneTone()).toBe('review');
    fixture.componentRef.setInput('taskState', '6-completed');
    expect(c.laneTone()).toBe('done');
    fixture.componentRef.setInput('taskState', '2-ready');
    expect(c.laneTone()).toBe('neutral');
  });

  it('renders the live-run pill with model + ticking duration when a run is active', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('project', 'demo-project');
    fixture.componentRef.setInput('runActive', true);
    fixture.componentRef.setInput('runModel', 'claude-opus-4-8');
    fixture.componentRef.setInput('runStartedAt', new Date(Date.now() - 90_000).toISOString());
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-run"]')).not.toBeNull();
    expect(text(fixture, 'orch-context-run-model')).toBe('opus 4.8');
    // 90s elapsed -> "1m" bucket.
    expect(text(fixture, 'orch-context-run-duration')).toBe('1m');
  });

  it('renders the pinned chip and reflects pinned + context key as data attributes (MC-2)', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('project', 'demo-project');
    fixture.componentRef.setInput('contextKey', 'project:demo-project');
    fixture.detectChanges();
    // Not pinned: no chip, no data-pinned attribute.
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-pin"]')).toBeNull();
    const headerUnpinned = fixture.nativeElement.querySelector('[data-testid="orch-context-header"]');
    expect(headerUnpinned.getAttribute('data-pinned')).toBeNull();
    expect(headerUnpinned.getAttribute('data-context-key')).toBe('project:demo-project');

    fixture.componentRef.setInput('pinned', true);
    fixture.detectChanges();
    expect(text(fixture, 'orch-context-pin')).toContain('Pinned');
    const headerPinned = fixture.nativeElement.querySelector('[data-testid="orch-context-header"]');
    expect(headerPinned.getAttribute('data-pinned')).toBe('true');
  });

  it('hides the run duration when no start timestamp is known', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('project', 'demo-project');
    fixture.componentRef.setInput('runActive', true);
    fixture.componentRef.setInput('runModel', 'claude-sonnet-5');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-run"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-run-duration"]')).toBeNull();
    expect(fixture.componentInstance.runDurationLabel()).toBeNull();
  });
});

describe('formatElapsed', () => {
  it('formats seconds under a minute', () => {
    expect(formatElapsed(0)).toBe('0s');
    expect(formatElapsed(45_000)).toBe('45s');
    expect(formatElapsed(59_999)).toBe('59s');
  });

  it('formats whole minutes under an hour', () => {
    expect(formatElapsed(60_000)).toBe('1m');
    expect(formatElapsed(59 * 60_000)).toBe('59m');
  });

  it('formats hours and minutes past an hour', () => {
    expect(formatElapsed(60 * 60_000)).toBe('1h');
    expect(formatElapsed(90 * 60_000)).toBe('1h 30m');
  });
});

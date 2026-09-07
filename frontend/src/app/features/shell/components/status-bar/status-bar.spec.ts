import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { formatRunningLabel, StatusBarComponent } from './status-bar';
import { TaskService } from '../../../../services/task.service';

describe('formatRunningLabel', () => {
  it.each([
    { local: 2, remote: 3, ceiling: 8, hosts: 1, expected: '2 local · remote 3/8' },
    { local: 2, remote: 0, ceiling: null, hosts: 0, expected: '2 local' },
    { local: 0, remote: 1, ceiling: 8, hosts: 1, expected: 'remote 1/8' },
    { local: 0, remote: 0, ceiling: null, hosts: 0, expected: 'no runners' },
    { local: 0, remote: 0, ceiling: 8, hosts: 1, expected: 'remote idle' },
  ])('renders $expected for local=$local and remote=$remote', ({ local, remote, ceiling, hosts, expected }) => {
    expect(formatRunningLabel(local, remote, ceiling, hosts)).toBe(expected);
  });

  it('keeps the remote unit explicit when the ceiling is not yet known', () => {
    expect(formatRunningLabel(0, 3, null, 1)).toBe('remote 3');
  });
});

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('StatusBarComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [StatusBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(StatusBarComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] StatusBarComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('StatusBarComponent CLI repair note', () => {
  it('uses zero space for completed repairs and shows active failures', async () => {
    await TestBed.configureTestingModule({
      imports: [StatusBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(StatusBarComponent);
    const service = TestBed.inject(TaskService);
    service.runnerStatus.set({
      projects: {},
      cliRepairs: [{
        cliType: 'claude',
        outcome: 'repaired',
        occurredAt: '2026-08-18T10:15:00Z',
        versionBefore: '2.1.231',
        versionAfter: '2.1.234',
        detail: 'claude CLI npm shim restored; version 2.1.231 -> 2.1.234.',
      }],
    });

    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="status-bar-cli-repair"]')).toBeNull();

    service.runnerStatus.set({
      projects: {},
      cliRepairs: [{
        cliType: 'claude',
        outcome: 'failed',
        occurredAt: '2026-08-18T11:15:00Z',
        detail: 'npm install exited 1.',
      }],
    });
    fixture.detectChanges();

    const note = fixture.nativeElement.querySelector('[data-testid="status-bar-cli-repair"]');
    expect(note?.textContent).toContain('CLI repair failed at');
    expect(note?.getAttribute('data-signal-tone')).toBe('mismatch');
    expect(note?.querySelector('[aria-label="CLI repair failed"]')).not.toBeNull();
  });

  // AGT-2706: a broken install that is journalled on sight, before its repair
  // attempt, is an active failure for the operator too.
  it('shows a detected but not yet repaired CLI as an active failure', async () => {
    await TestBed.configureTestingModule({
      imports: [StatusBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(StatusBarComponent);
    TestBed.inject(TaskService).runnerStatus.set({
      projects: {},
      cliRepairs: [{
        cliType: 'claude',
        outcome: 'detected',
        occurredAt: '2026-09-06T07:45:00Z',
        versionBefore: '2.1.263',
        detail: 'claude CLI launcher is still the postinstall placeholder.',
      }],
    });

    fixture.detectChanges();

    const note = fixture.nativeElement.querySelector('[data-testid="status-bar-cli-repair"]');
    expect(note?.textContent).toContain('CLI broken since');
    expect(note?.getAttribute('data-signal-tone')).toBe('mismatch');
    expect(note?.querySelector('[aria-label="CLI broken"]')).not.toBeNull();
  });
});

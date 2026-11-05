import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { ExecutionInfrastructureComponent } from './execution-infrastructure';

describe('ExecutionInfrastructureComponent', () => {
  it('shows current use and saves capacity for the next gate', async () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(),
      provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(ExecutionInfrastructureComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/system/execution-infrastructure').flush({
      gate: { host: 'gate-1', transport: 'ssh', busy: 1, total: 5,
        reachability: 'reachable', lastReachedAt: null, lastFailureAt: null,
        capacity: { slots: 5, processorsPerGate: 6, hostCores: 30,
          allowOversubscription: false }, capacitySource: 'configuration', workers: [] },
      taskServer: 'local monolith', workstation: 'local',
      engine: { state: 'ready', required: true, lastContact: null, lastEngineId: null },
    });
    await fixture.whenStable();
    fixture.detectChanges();
    const form = fixture.nativeElement.querySelector('[data-testid="gate-capacity-settings"]') as HTMLElement;
    expect(form.textContent).toContain('1 of 5 reported host slots in use');
    const slots = form.querySelector('input[name="slots"]') as HTMLInputElement;
    slots.value = '4';
    slots.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(form.textContent).toContain('1 of 5 reported host slots in use');
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    const request = http.expectOne('/api/system/gate-capacity');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toMatchObject({ slots: 4, processorsPerGate: 6, hostCores: 30 });
    request.flush({ capacity: request.request.body });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(form.textContent).toContain('Saved');
    expect(form.textContent).toContain('1 of 5 reported host slots in use');
    http.verify();
  });
});

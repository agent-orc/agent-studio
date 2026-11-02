import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { RemoteHost } from '../../models/remote-host.model';
import { groupPhysicalHosts } from '../../models/physical-host-group';
import { EnvironmentHostListComponent } from './environment-host-list';

const ROLE = {
  id: 'coding-1', name: 'agent-runner-01', capacityHostId: 'agent-runner-01',
  role: 'remote', serviceRole: 'coding', status: 'online', roleMaxParallelism: 8,
  activeTaskCount: 2,
} as RemoteHost;

function mount() {
  TestBed.configureTestingModule({
    imports: [EnvironmentHostListComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(EnvironmentHostListComponent);
  fixture.componentRef.setInput('groups', groupPhysicalHosts([ROLE], false));
  fixture.componentRef.setInput('gates', [{
    id: 'gate-1', name: 'agent-runner-01', busy: 1, slots: 3, lastHeartbeat: '2026-11-01T22:03:00Z',
  }]);
  fixture.componentRef.setInput('slots', (host: RemoteHost) => host.activeTaskCount ?? 0);
  fixture.detectChanges();
  return fixture;
}

describe('EnvironmentHostListComponent', () => {
  it('includes a colocated gate worker in the machine slot total', () => {
    const fixture = mount();
    const machine = fixture.nativeElement.querySelector('[data-testid="remote-host-primary-row"]');
    expect(machine.textContent).toContain('3 / 11');
    expect(fixture.nativeElement.querySelectorAll('[data-host-row]').length).toBe(3);
  });

  it('moves selection between contained rows with the arrow keys', () => {
    const fixture = mount();
    const rows = fixture.nativeElement.querySelectorAll('[data-host-row]') as NodeListOf<HTMLButtonElement>;
    const selected: string[] = [];
    fixture.componentInstance.selectedChange.subscribe(id => selected.push(id));
    rows[0].focus();
    rows[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    expect(document.activeElement).toBe(rows[1]);
    expect(selected).toEqual(['coding-1']);
  });
});

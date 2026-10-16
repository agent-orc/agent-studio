import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { EnvironmentMapComponent } from './environment-map';

describe('EnvironmentMapComponent', () => {
  it('renders machine frames and emits the selected role identity', async () => {
    await TestBed.configureTestingModule({ imports: [EnvironmentMapComponent], providers: [provideZonelessChangeDetection()] }).compileComponents();
    const fixture = TestBed.createComponent(EnvironmentMapComponent);
    const roles = ['control', 'coding', 'review', 'gate', 'integration', 'link'] as const;
    const types = ['outbound claim link', 'reverse tunnel', 'SSH gate transport', 'Git remote'] as const;
    fixture.componentRef.setInput('map', {
      machines: [{ id: 'host', name: 'agent-runner-01', nodes: roles.map(role => ({
        id: role, label: role === 'coding' ? 'Coding slot' : role, role,
        health: 'online', capacity: '1/2 slots', version: '1.0', drift: 'current', lastContact: 'now',
        repositoryAccess: role === 'coding' ? 'writes' : 'unknown', hostId: role === 'coding' ? 'coding' : null,
      })) }],
      connections: types.map((type, index) => ({ id: `${index}`, from: 'control', to: 'host', type, health: 'ok' })),
    });
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelectorAll('[data-testid="environment-machine"]')).toHaveLength(1);
    expect(element.querySelector('[data-testid="environment-connection"]')?.textContent).toContain('outbound claim link');
    expect(element.querySelectorAll('[data-testid="environment-topology-edge"]')).toHaveLength(4);
    expect(element.querySelectorAll('[data-testid="environment-topology-point"]')).toHaveLength(2);
    for (const role of roles) expect(element.querySelector(`[data-testid="environment-node"][data-role="${role}"]`)).toBeTruthy();
    for (const type of types) expect(element.querySelector('[data-testid="environment-map"]')?.textContent).toContain(type);
    expect(element.querySelector('[data-testid="environment-repository-access"][data-access="writes"]')?.textContent).toContain('writes');
    const selected: string[] = [];
    fixture.componentInstance.selectHost.subscribe(id => selected.push(id));
    (element.querySelector('button[aria-label="Open Coding slot details"]') as HTMLButtonElement).click();
    expect(selected).toEqual(['coding']);
    fixture.destroy();
  });
});

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { EnvironmentFlowComponent } from './environment-flow.component';

describe('EnvironmentFlowComponent queue semantics', () => {
  it('renders integration and human acceptance as independent queues', async () => {
    await TestBed.configureTestingModule({
      imports: [EnvironmentFlowComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(EnvironmentFlowComponent);
    fixture.componentRef.setInput('tasks', []);
    fixture.componentInstance.flow.set({
      capturedAt: '2026-11-27T12:00:00Z',
      today: { created: 0, delivered: 3, reviewed: 3, integrated: 2, deployed: null,
        completed: 1, readyToIntegrationMedianMinutes: null },
      lanes: [],
      queues: [
        { queue: 'Integration', waiting: 3, oldestWaitMinutes: 55, throughputPerHour: 2 / 24 },
        { queue: 'Acceptance', waiting: 1, oldestWaitMinutes: 120, throughputPerHour: 1 / 24 },
        { queue: 'Integration (local lane fallback)', waiting: null, oldestWaitMinutes: null, throughputPerHour: 0 },
      ],
      waitingReasons: [{ reason: 'Waiting for integration', count: 2 }, { reason: 'Waiting for acceptance', count: 1 }],
    });
    fixture.detectChanges();
    const rows = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr'));
    expect(rows.map(row => Array.from(row.children).map(cell => cell.textContent?.trim()))).toEqual([
      ['Integration', '3', '55 min', '0.1'],
      ['Acceptance', '1', '2.0 h', '0.0'],
      ['Integration (local lane fallback)', '—', 'Unavailable', '0.0'],
    ]);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Waiting for integration');
    fixture.destroy();
  });
});

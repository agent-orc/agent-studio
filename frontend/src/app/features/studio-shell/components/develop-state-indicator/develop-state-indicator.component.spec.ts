import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { DevelopStateIndicatorComponent } from './develop-state-indicator.component';

describe('DevelopStateIndicatorComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({
    imports: [DevelopStateIndicatorComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }));

  it('keeps a line stop acute while a verification run is running', async () => {
    const fixture = TestBed.createComponent(DevelopStateIndicatorComponent);
    fixture.componentRef.setInput('project', 'demo');
    fixture.detectChanges();
    const requests = TestBed.inject(HttpTestingController);
    requests.expectOne('/api/develop-states').flush([{ project: 'demo', developState: 'running', lineStop: 'Prepare failed', quarantinedCount: 2 }]);
    await fixture.whenStable();
    expect(fixture.componentInstance.tone()).toBe('danger');
    expect(fixture.componentInstance.label()).toBe('Develop stopped · Prepare failed');
    requests.verify();
    fixture.destroy();
  });

  it('does not interpret a legacy red value as a line-stop decision', async () => {
    const fixture = TestBed.createComponent(DevelopStateIndicatorComponent);
    fixture.detectChanges();
    const requests = TestBed.inject(HttpTestingController);
    requests.expectOne('/api/develop-states').flush([{ project: 'demo', developState: 'red', lineStop: null, quarantinedCount: 1 }]);
    await fixture.whenStable();
    expect(fixture.componentInstance.tone()).toBe('warning');
    expect(fixture.componentInstance.label()).toBe('Develop green · 1 quarantined');
    requests.verify();
    fixture.destroy();
  });
});

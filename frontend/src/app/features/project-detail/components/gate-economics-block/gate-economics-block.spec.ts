import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, expect, it } from 'vitest';
import { GateEconomicsBlockComponent } from './gate-economics-block';

afterEach(() => TestBed.inject(HttpTestingController).verify());

it('keeps the newest requested window when responses complete out of order', () => {
  TestBed.configureTestingModule({
    imports: [GateEconomicsBlockComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(GateEconomicsBlockComponent);
  const http = TestBed.inject(HttpTestingController);
  const first = http.expectOne('/api/gate-economics?window=7d');
  fixture.componentInstance.setWindow('30d');
  const next = http.expectOne('/api/gate-economics?window=30d');
  next.flush({ window: '30d', rows: [] });
  first.flush({ window: '7d', rows: [] });
  expect(fixture.componentInstance.data()?.window).toBe('30d');
  fixture.destroy();
});

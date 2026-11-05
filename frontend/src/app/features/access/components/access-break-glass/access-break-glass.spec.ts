import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { AccessBreakGlassComponent } from './access-break-glass';

describe('AccessBreakGlassComponent', () => {
  it('mounts the Owner control', async () => {
    await TestBed.configureTestingModule({
      imports: [AccessBreakGlassComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(AccessBreakGlassComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="access-break-glass"]')).toBeTruthy();
  });
});

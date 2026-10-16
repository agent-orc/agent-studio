import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { AuthSessionState } from '../../../../services/auth.service';
import { DeploymentCheckpointsComponent } from './deployment-checkpoints';

describe('DeploymentCheckpointsComponent', () => {
  it('does not request Task Server checkpoint routes in the local profile', async () => {
    TestBed.configureTestingModule({
      imports: [DeploymentCheckpointsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(AuthSessionState).status.set({
      profile: 'local', bootstrapRequired: false, authenticated: true,
    });
    const fixture = TestBed.createComponent(DeploymentCheckpointsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(TestBed.inject(HttpTestingController).match(() => true)).toEqual([]);
    expect(fixture.nativeElement.textContent).toContain('not applicable in the local profile');
    expect(fixture.nativeElement.textContent).not.toContain('without canonical repository');
  });
});

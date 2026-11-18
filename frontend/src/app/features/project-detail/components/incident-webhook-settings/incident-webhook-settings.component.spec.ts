import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthSessionState } from '../../../../services/auth.service';
import { IncidentWebhookSettingsComponent } from './incident-webhook-settings.component';

describe('IncidentWebhookSettingsComponent', () => {
  it('saves, tests and removes a project webhook without showing its secret URL', async () => {
    await TestBed.configureTestingModule({
      imports: [IncidentWebhookSettingsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(IncidentWebhookSettingsComponent);
    fixture.componentRef.setInput('projectName', 'Shop');
    TestBed.inject(AuthSessionState).status.set({ profile: 'local', bootstrapRequired: false, authenticated: true });
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Shop/incident-webhook').flush({ configured: false, destination: '' });
    component.url.set('https://hooks.example/incident?token=secret');

    component.save();
    const save = http.expectOne('/api/projects/Shop/incident-webhook');
    expect(save.request.body).toEqual({
      url: 'https://hooks.example/incident?token=secret', studioBaseUrl: window.location.origin,
    });
    save.flush({ configured: true, destination: 'hooks.example' });
    expect(component.destination()).toBe('hooks.example');
    expect(component.url()).toBe('');

    component.test();
    http.expectOne('/api/projects/Shop/incident-webhook/test').flush({ delivered: true });
    expect(component.message()).toBe('Test message delivered.');

    component.remove();
    http.expectOne('/api/projects/Shop/incident-webhook').flush({ configured: false, destination: '' });
    expect(component.destination()).toBe('');
  });
});

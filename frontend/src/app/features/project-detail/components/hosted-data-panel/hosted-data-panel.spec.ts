import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { HostedDataPanelComponent } from './hosted-data-panel';

describe('HostedDataPanelComponent', () => {
  const hostedData = (project: string) => ({
    database: { secretRef: `secret://${project}/DATABASE_URL`, environmentVariable: 'DATABASE_URL' },
    volumes: [{ name: 'uploads', mountPath: `/${project}/uploads` }],
    schedules: [], audit: [], adapterReady: false,
  });

  it('shows the secret reference as metadata and saves a volume schedule', async () => {
    await TestBed.configureTestingModule({
      imports: [HostedDataPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(HostedDataPanelComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/demo/environments').flush({
      environments: [{ declaration: { id: 'stage', name: 'Stage' } }],
    });
    http.expectOne('/api/projects/demo/environments/stage/volumes').flush({
      database: { secretRef: 'secret://demo/stage/DATABASE_URL', environmentVariable: 'DATABASE_URL' },
      volumes: [{ name: 'uploads', mountPath: '/app/uploads' }], schedules: [], audit: [], adapterReady: false,
    });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('secret://demo/stage/DATABASE_URL');
    expect(fixture.nativeElement.textContent).toContain('no volume host backup adapter');
    const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
    select.value = '8';
    select.dispatchEvent(new Event('change'));
    const request = http.expectOne('/api/projects/demo/environments/stage/volumes/uploads/schedule');
    expect(request.request.body).toEqual({ hourUtc: 8 });
    request.flush({});
    http.expectOne('/api/projects/demo/environments').flush({ environments: [] });
    await fixture.whenStable();
    http.verify();
  });

  it('keeps the active project visible when an older volume response arrives later', async () => {
    await TestBed.configureTestingModule({
      imports: [HostedDataPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(HostedDataPanelComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('projectName', 'first');
    fixture.detectChanges();
    http.expectOne('/api/projects/first/environments').flush({
      environments: [{ declaration: { id: 'stage', name: 'First stage' } }],
    });
    const oldVolume = http.expectOne('/api/projects/first/environments/stage/volumes');

    fixture.componentRef.setInput('projectName', 'second');
    fixture.detectChanges();
    expect(fixture.componentInstance.environments()).toEqual([]);
    http.expectOne('/api/projects/second/environments').flush({
      environments: [{ declaration: { id: 'stage', name: 'Second stage' } }],
    });
    http.expectOne('/api/projects/second/environments/stage/volumes').flush(hostedData('second'));
    oldVolume.flush(hostedData('first'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('secret://second/DATABASE_URL');
    expect(fixture.nativeElement.textContent).not.toContain('secret://first/DATABASE_URL');
    expect(fixture.componentInstance.loading()).toBe(false);
    http.verify();
  });

  it('ignores an old project error after the active project loads', async () => {
    await TestBed.configureTestingModule({
      imports: [HostedDataPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(HostedDataPanelComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('projectName', 'first');
    fixture.detectChanges();
    const oldList = http.expectOne('/api/projects/first/environments');
    fixture.componentRef.setInput('projectName', 'second');
    fixture.detectChanges();
    http.expectOne('/api/projects/second/environments').flush({ environments: [] });
    oldList.flush('unavailable', { status: 503, statusText: 'Unavailable' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toBeNull();
    expect(fixture.componentInstance.loading()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain('No environments are declared.');
    http.verify();
  });
});

import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { HostedDataPanelComponent } from './hosted-data-panel';

describe('HostedDataPanelComponent', () => {
  const stageList = { environments: [{ declaration: { id: 'stage', name: 'Stage' } }] };
  const hostedData = (secretRef: string) => ({
    database: { secretRef, environmentVariable: 'DATABASE_URL' },
    volumes: [{ name: 'uploads', mountPath: '/app/uploads' }], schedules: [], audit: [], adapterReady: false,
  });

  async function showProject(name: string) {
    await TestBed.configureTestingModule({
      imports: [HostedDataPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(HostedDataPanelComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('projectName', name);
    fixture.detectChanges();
    http.expectOne(`/api/projects/${name}/environments`).flush(stageList);
    http.expectOne(`/api/projects/${name}/environments/stage/volumes`).flush(hostedData(`secret://${name}`));
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, http };
  }

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

  it('cancels the previous project load and clears its data when the project changes', async () => {
    const { fixture, http } = await showProject('first');
    expect(fixture.nativeElement.textContent).toContain('secret://first');

    fixture.componentInstance.load('first');
    http.expectOne('/api/projects/first/environments').flush(stageList);
    const staleVolumes = http.expectOne('/api/projects/first/environments/stage/volumes');

    fixture.componentRef.setInput('projectName', 'second');
    fixture.detectChanges();
    expect(staleVolumes.cancelled).toBe(true);
    expect(fixture.nativeElement.textContent).not.toContain('secret://first');
    http.expectOne('/api/projects/second/environments').flush(stageList);
    http.expectOne('/api/projects/second/environments/stage/volumes').flush(hostedData('secret://second'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('secret://second');
    expect(fixture.nativeElement.textContent).not.toContain('secret://first');
    http.verify();
  });

  it('ignores a mutation result from the previous project after switching projects', async () => {
    const { fixture, http } = await showProject('first');
    fixture.componentInstance.setSchedule('stage', 'uploads', { target: { value: '8' } } as unknown as Event);
    const staleMutation = http.expectOne('/api/projects/first/environments/stage/volumes/uploads/schedule');

    fixture.componentRef.setInput('projectName', 'second');
    fixture.detectChanges();
    http.expectOne('/api/projects/second/environments').flush(stageList);
    http.expectOne('/api/projects/second/environments/stage/volumes').flush(hostedData('secret://second'));
    staleMutation.flush({});
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.componentInstance.busy()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain('secret://second');
    expect(fixture.nativeElement.textContent).not.toContain('Backup schedule saved.');
    http.verify();
  });

  it('ignores a previous project mutation after switching away and back', async () => {
    const { fixture, http } = await showProject('first');
    fixture.componentInstance.backup('stage', 'uploads');
    const staleMutation = http.expectOne('/api/projects/first/environments/stage/volumes/uploads/backups');

    fixture.componentRef.setInput('projectName', 'second');
    fixture.detectChanges();
    fixture.componentRef.setInput('projectName', 'first');
    fixture.detectChanges();
    http.expectOne('/api/projects/second/environments');
    http.expectOne('/api/projects/first/environments').flush(stageList);
    http.expectOne('/api/projects/first/environments/stage/volumes').flush(hostedData('secret://first'));
    staleMutation.flush({});
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('Backup completed.');
    http.verify();
  });
});

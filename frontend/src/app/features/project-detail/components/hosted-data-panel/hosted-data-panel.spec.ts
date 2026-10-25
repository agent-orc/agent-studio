import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { HostedDataPanelComponent } from './hosted-data-panel';

describe('HostedDataPanelComponent', () => {
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
});

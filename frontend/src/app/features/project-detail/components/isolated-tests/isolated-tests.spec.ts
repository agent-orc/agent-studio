import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { IsolatedTestsComponent } from './isolated-tests';

describe('IsolatedTestsComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({
    imports: [IsolatedTestsComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }));

  it('ignores an older project response and emits the owning card', async () => {
    const fixture = TestBed.createComponent(IsolatedTestsComponent);
    fixture.componentRef.setInput('projectName', 'old');
    fixture.detectChanges();
    const requests = TestBed.inject(HttpTestingController);
    const older = requests.expectOne('/api/projects/old/test-quarantines');
    fixture.componentRef.setInput('projectName', 'new');
    fixture.detectChanges();
    requests.expectOne('/api/projects/new/test-quarantines').flush([{
      id: 'new', test: 'NewTests.Fails', kind: 'held', card: 'AGT-1', coveringFolders: ['runner'],
      expiresAtUtc: new Date(Date.now() + 3600000).toISOString(),
    }]);
    older.flush([]);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('NewTests.Fails');
    const cards: string[] = [];
    fixture.componentInstance.openCard.subscribe(card => cards.push(card));
    host.querySelector<HTMLAnchorElement>('a')!.click();
    expect(cards).toEqual(['AGT-1']);
    requests.verify();
    fixture.destroy();
  });
});

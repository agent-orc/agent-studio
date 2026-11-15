import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DetailLoadErrorComponent } from './detail-load-error.component';

describe('DetailLoadErrorComponent', () => {
  let fixture: ComponentFixture<DetailLoadErrorComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DetailLoadErrorComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(DetailLoadErrorComponent);
    fixture.componentRef.setInput('taskLabel', 'WEB-10');
    fixture.componentRef.setInput('message', 'The detail request failed.');
    fixture.detectChanges();
  });

  it('shows the failed task and emits retry', () => {
    const retried = vi.fn();
    fixture.componentInstance.retry.subscribe(retried);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('WEB-10');
    const state = root.querySelector('[data-testid="task-detail-load-error-state"]');
    expect(state?.getAttribute('data-tone')).toBe('danger');
    expect(state?.textContent?.trim()).toBe('Task detail unavailable');
    root.querySelector<HTMLButtonElement>('[data-testid="task-detail-load-retry"]')?.click();
    expect(retried).toHaveBeenCalledOnce();
  });
});

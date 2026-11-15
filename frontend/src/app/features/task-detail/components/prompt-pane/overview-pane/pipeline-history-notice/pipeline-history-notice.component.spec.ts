import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PipelineHistoryNoticeComponent } from './pipeline-history-notice.component';

describe('PipelineHistoryNoticeComponent', () => {
  let fixture: ComponentFixture<PipelineHistoryNoticeComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PipelineHistoryNoticeComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(PipelineHistoryNoticeComponent);
    fixture.componentRef.setInput('attempt', 2);
    fixture.componentRef.setInput('currentAttempt', 3);
    fixture.detectChanges();
  });

  it('identifies the selected attempt as superseded and names the current attempt', () => {
    const notice = fixture.nativeElement.querySelector(
      '[data-testid="overview-pipeline-superseded"]',
    ) as HTMLElement;

    expect(notice.textContent).toContain('Attempt #2 · superseded');
    const state = notice.querySelector('[data-testid="overview-pipeline-superseded-state"]') as HTMLElement;
    expect(state.dataset['tone']).toBe('neutral');
    expect(notice.textContent).toContain('Current status colours belong to Attempt #3');
  });
});

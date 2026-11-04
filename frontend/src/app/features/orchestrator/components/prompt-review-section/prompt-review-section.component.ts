import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { PromptDetail } from '../../../../services/prompt-admin.service';
import { promptFindingTone, promptReviewStatus } from './prompt-review-status';

@Component({
  selector: 'app-prompt-review-section',
  standalone: true,
  imports: [DatePipe, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-review-section.component.html',
  styleUrl: './prompt-review-section.component.scss',
})
export class PromptReviewSectionComponent {
  readonly detail = input.required<PromptDetail>();
  readonly busy = input(false);
  readonly reviewRequest = output<void>();

  readonly reviewStatus = promptReviewStatus;
  readonly findingTone = promptFindingTone;
}

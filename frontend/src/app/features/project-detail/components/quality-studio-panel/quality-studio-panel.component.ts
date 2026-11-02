import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { QualityStudioStatusService } from './quality-studio-status.service';

@Component({
  selector: 'app-quality-studio-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './quality-studio-panel.component.html',
  styleUrl: './quality-studio-panel.component.scss',
})
export class QualityStudioPanelComponent {
  readonly registration = inject(QualityStudioStatusService);
}

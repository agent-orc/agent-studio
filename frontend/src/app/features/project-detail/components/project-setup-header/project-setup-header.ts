import { ChangeDetectionStrategy, Component, output } from '@angular/core';
import { SettingsJumpLinksComponent } from '../settings-jump-links/settings-jump-links';

/** Navigation map for the project setup landing view. */
@Component({
  selector: 'app-project-setup-header',
  standalone: true,
  imports: [SettingsJumpLinksComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-setup-header.html',
  styleUrl: './project-setup-header.scss',
})
export class ProjectSetupHeaderComponent {
  readonly reviewAspects = output<void>();
  readonly release = output<void>();
}

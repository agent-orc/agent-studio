import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import type { ProjectSidebarRow } from '../../studio-shell.project-rows';
import { StudioEmptyStateComponent } from '../studio-empty-state/studio-empty-state.component';

@Component({
  selector: 'app-studio-welcome',
  standalone: true,
  imports: [EmptyStateComponent, StudioEmptyStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './studio-welcome.component.html',
  styleUrl: './studio-welcome.component.scss',
})
export class StudioWelcomeComponent {
  readonly projects = input.required<readonly ProjectSidebarRow[]>();
  readonly boardOpened = output<string>();
  readonly deploymentDashboardOpened = output<void>();
  readonly chatOpened = output<void>();
}

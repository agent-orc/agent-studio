import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import {
  WikiMetaPanelStateService,
  WikiMetaSectionId,
} from '../wiki-meta-panel-state.service';

/** Shared accessible disclosure shell for one section in the wiki meta rail. */
@Component({
  selector: 'app-wiki-meta-section',
  standalone: true,
  imports: [StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-meta-section.component.html',
  styleUrl: './wiki-meta-section.component.scss',
})
export class WikiMetaSectionComponent {
  readonly sectionId = input.required<WikiMetaSectionId>();
  readonly title = input.required<string>();
  readonly toggleTestId = input.required<string>();
  readonly bodyId = input.required<string>();

  private readonly state = inject(WikiMetaPanelStateService);

  collapsed(): boolean {
    return this.state.isSectionCollapsed(this.sectionId());
  }

  toggle(): void {
    this.state.toggleSection(this.sectionId());
  }
}

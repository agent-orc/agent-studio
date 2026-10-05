import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TagFiltersComponent } from '../../../../../components/tag-filters/tag-filters.component';

@Component({
  selector: 'app-wiki-tag-controls',
  standalone: true,
  imports: [TagFiltersComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-tag-controls.component.html',
  styleUrl: './wiki-tag-controls.component.scss',
})
export class WikiTagControlsComponent {
  readonly projectName = input.required<string>();
  readonly openGlossaries = output<void>();
}

import { ChangeDetectionStrategy, Component, computed, inject, input, ViewEncapsulation } from '@angular/core';
import type { PaneName } from '../../services/layout-panes.service';
import { LayoutPanesService } from '../../services/layout-panes.service';
import { GitPaneService } from '../../services/git-pane.service';
import { SegmentedControlComponent, type SegmentedOption } from '../../../../components/segmented-control/segmented-control.component';

@Component({
  selector: 'app-mobile-pane-switcher',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  imports: [SegmentedControlComponent],
  templateUrl: './mobile-pane-switcher.component.html',
  styleUrl: './mobile-pane-switcher.component.scss',
})
export class MobilePaneSwitcherComponent {
  private readonly layout = inject(LayoutPanesService);
  private readonly git = inject(GitPaneService);
  readonly showGit = input(true);
  readonly selected = computed(() => this.showGit() || this.layout.mobilePane() !== 'git'
    ? this.layout.mobilePane()
    : this.layout.panesVisible().prompt ? 'prompt' : 'protocol');
  private readonly allOptions: readonly SegmentedOption<PaneName>[] = [
    { value: 'prompt', label: 'Overview', testid: 'detail-mobile-pane-overview' },
    { value: 'protocol', label: 'Result', testid: 'detail-mobile-pane-result' },
    { value: 'git', label: 'Git', testid: 'detail-mobile-pane-git' },
  ];
  readonly options = computed(() => this.showGit() ? this.allOptions : this.allOptions.filter(option => option.value !== 'git'));

  selectPane(name: PaneName): void {
    this.layout.selectMobilePane(name);
    if (name === 'git' && !this.git.status()) this.git.refresh();
  }
}

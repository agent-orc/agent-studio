import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { ProjectDeployDataService, type ProjectDeploySnapshot, type Release, type ReleaseNotes } from '../../services/project-deploy-data.service';

@Component({
  selector: 'app-project-deploy-releases',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './project-deploy-releases.html',
  styleUrl: '../project-deploy-area/project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployReleasesComponent {
  private readonly data = inject(ProjectDeployDataService);
  readonly snapshot = input.required<ProjectDeploySnapshot>();
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed<Release | null>(() => this.snapshot().releases.find(r => r.id === this.selectedId()) ?? this.snapshot().releases[0] ?? null);
  readonly notes = signal<ReleaseNotes | null>(null);
  readonly notesError = signal(false);

  constructor() {
    effect(onCleanup => {
      const release = this.selected();
      this.notes.set(null);
      this.notesError.set(false);
      if (!release || this.snapshot().source !== 'api') return;
      const subscription = this.data.notes(release.projectId, release.id).subscribe({
        next: notes => this.notes.set(notes),
        error: () => this.notesError.set(true),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }
  select(id: string): void { this.selectedId.set(id); }
}

import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { ProjectDeployDataService, serverMessage, type ReleaseNotes } from '../../services/project-deploy-data.service';
import type { DeployPanel, Release } from '../../state/deploy-area.model';

/**
 * Notes of the selected release: a person's product wording per card, else the
 * card title. Maintenance (chore) cards are listed apart, not as product notes (F18).
 */
@Component({
  selector: 'app-project-deploy-release-notes',
  standalone: true,
  templateUrl: './project-deploy-release-notes.html',
  styleUrl: './project-deploy-release-notes.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployReleaseNotesComponent {
  private readonly data = inject(ProjectDeployDataService);
  readonly release = input.required<Release>();
  readonly panel = signal<DeployPanel<ReleaseNotes>>({ status: 'loading' });
  readonly notes = computed(() => {
    const panel = this.panel();
    return panel.status === 'ready' ? panel.value : null;
  });

  readonly productNotes = computed(() => this.notes()?.notes.filter(note => !note.maintenance) ?? []);
  readonly maintenanceNotes = computed(() => this.notes()?.notes.filter(note => note.maintenance) ?? []);
  readonly maintenanceKeys = computed(() => this.maintenanceNotes().map(note => note.cardKey).join(', '));
  readonly editing = signal<string | null>(null);
  readonly draft = signal('');
  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);

  edit(cardKey: string, text: string): void {
    this.saveError.set(null);
    this.draft.set(text);
    this.editing.set(cardKey);
  }

  save(cardKey: string): void {
    const release = this.release();
    this.saving.set(true);
    this.data.editReleaseNote(release.projectId, release.id, cardKey, this.draft().trim()).subscribe({
      next: value => {
        this.saving.set(false);
        this.editing.set(null);
        if (value.releaseId === this.release().id) this.panel.set({ status: 'ready', value });
      },
      error: (error: unknown) => {
        this.saving.set(false);
        this.saveError.set(serverMessage(error) ?? 'The note could not be saved.');
      },
    });
  }

  constructor() {
    effect(onCleanup => {
      const release = this.release();
      this.editing.set(null);
      const subscription = this.data.releaseNotes(release.projectId, release.id)
        .subscribe(value => this.panel.set(value));
      onCleanup(() => subscription.unsubscribe());
    });
  }
}

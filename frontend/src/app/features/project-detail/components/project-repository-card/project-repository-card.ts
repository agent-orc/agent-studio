import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { Observable } from 'rxjs';
import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';

type Visibility = 'private' | 'public';

interface ProjectRepositorySettings {
  repositoryVisibility?: string | null;
  integrationBranch?: string | null;
}

/**
 * AGT-3580 - the project setting the readiness remedies for repository
 * visibility and the integration branch open. Both values were settable only
 * by an HTTP call the readiness line spelled out, so the operator fell back to
 * a script. Each field carries `data-setting-key` so a remedy link focuses it.
 */
@Component({
  selector: 'app-project-repository-card',
  standalone: true,
  imports: [SegmentedControlComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-repository-card.html',
  styleUrl: './project-repository-card.scss',
})
export class ProjectRepositoryCardComponent implements OnInit {
  readonly projectName = input.required<string>();

  private readonly http = inject(HttpClient);

  /** Null until recorded: Remote Review then treats the repository as public. */
  readonly visibility = signal<Visibility | null>(null);
  readonly branch = signal('');
  readonly savedBranch = signal('');
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly visibilityOptions = [
    { value: 'private' as const, label: 'Private', tooltip: 'Remote Review clones with a read credential scoped to this repository.', testid: 'project-repository-visibility-private' },
    { value: 'public' as const, label: 'Public', tooltip: 'Remote Review clones without credentials.', testid: 'project-repository-visibility-public' },
  ];

  ngOnInit(): void {
    this.http.get<Record<string, ProjectRepositorySettings>>('/api/projects/settings').subscribe({
      next: all => {
        const project = all?.[this.projectName()];
        const visibility = project?.repositoryVisibility;
        this.visibility.set(visibility === 'private' || visibility === 'public' ? visibility : null);
        this.branch.set(project?.integrationBranch ?? '');
        this.savedBranch.set(project?.integrationBranch ?? '');
      },
    });
  }

  setVisibility(visibility: Visibility): void {
    if (this.saving() || visibility === this.visibility()) return;
    const previous = this.visibility();
    this.visibility.set(visibility);
    this.save(
      this.http.put<{ repositoryVisibility?: string | null }>(this.url('repository-visibility'), { visibility }),
      `Repository visibility is recorded as ${visibility}.`,
      () => this.visibility.set(previous),
      'Repository visibility could not be saved. The previous value is still active.');
  }

  saveBranch(): void {
    const branch = this.branch().trim();
    if (this.saving() || branch === this.savedBranch()) return;
    this.save(
      this.http.put<ProjectRepositorySettings>(this.url('integration-branch'), { branch }),
      branch ? `Integration branch is set to ${branch}.` : 'Integration branch follows origin/HEAD again.',
      () => this.branch.set(this.savedBranch()),
      'Integration branch could not be saved. The previous branch is still active.',
      () => this.savedBranch.set(branch));
  }

  private save(request: Observable<unknown>, done: string, rollback: () => void, failure: string,
    applied?: () => void): void {
    this.saving.set(true);
    this.error.set(null);
    this.notice.set(null);
    request.subscribe({
      next: () => { applied?.(); this.saving.set(false); this.notice.set(`${done} Recheck readiness to confirm.`); },
      error: () => { rollback(); this.saving.set(false); this.error.set(failure); },
    });
  }

  private url(setting: string): string {
    return `/api/projects/${encodeURIComponent(this.projectName())}/${setting}`;
  }
}

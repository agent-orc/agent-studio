import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { TagRegistryStore } from '../../services/tag-registry.store';
import { TagProposalsService, TagProposalSubjectKind } from '../../services/tag-proposals.service';

@Component({
  selector: 'app-tag-proposals',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './tag-proposals.component.html',
  styleUrl: './tag-proposals.component.scss',
})
export class TagProposalsComponent {
  readonly projectName = input.required<string>();
  readonly subjectKind = input.required<TagProposalSubjectKind>();
  readonly subjectId = input.required<string>();
  readonly taggingStatus = input<string | null>(null);
  private readonly service = inject(TagProposalsService);
  private readonly registry = inject(TagRegistryStore);
  readonly busy = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly pending = computed(() => this.service.proposals().filter(item =>
    item.projectName === this.projectName() && item.subjectKind === this.subjectKind()
    && item.subjectId === this.subjectId() && item.state === 'pending'));
  readonly statusWithoutDetails = computed(() => this.taggingStatus() === 'tags-proposed'
    && !this.service.proposals().some(item => item.projectName === this.projectName()
      && item.subjectKind === this.subjectKind() && item.subjectId === this.subjectId()));
  constructor() { effect(() => {
    const project = this.projectName();
    this.service.load(project);
    if (this.pending().length) this.registry.ensureProject(project);
  }); }
  label(id: string): string { return this.registry.byIdForProject(this.projectName()).get(id)?.label ?? id; }
  labels(ids: readonly string[]): string { return ids.map(id => this.label(id)).join(', '); }
  decide(id: string, choice: 'accept' | 'reject'): void {
    this.busy.set(id); this.error.set(null);
    this.service.decide(this.projectName(), id, choice).subscribe({
      next: () => this.busy.set(null),
      error: () => { this.busy.set(null); this.error.set('Tag proposal could not be saved.'); },
    });
  }
}

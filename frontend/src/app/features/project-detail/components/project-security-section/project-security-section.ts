import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { SecurityMeta, SecurityOverview } from '../../../../models/project-docs.model';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';

/**
 * Project-level Security section: meta header (last review, rating,
 * one-line summary), MD file browser, inline view + edit.
 *
 * Prototype: no diff view, no syntax highlight, no Markdown preview
 * toolbar. Source-mode textarea for editing, rendered Markdown in
 * read mode. Files live under <repoRoot>/docs/operations/security/.
 */
@Component({
  selector: 'app-project-security-section',
  standalone: true,
  imports: [FormsModule, MarkdownViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-security-section.html',
  styleUrl: './project-security-section.scss'
})
export class ProjectSecuritySectionComponent {
  readonly projectName = input.required<string>();

  private readonly docs = inject(ProjectDocsService);

  readonly overview = signal<SecurityOverview | null>(null);
  readonly loading = signal(false);
  readonly openedRel = signal<string | null>(null);
  readonly openedContent = signal<string>('');
  readonly editing = signal(false);
  readonly newFileMode = signal(false);

  newFileName = '';
  editorDraft = '';
  metaDraft: SecurityMeta = { lastReviewDate: null, rating: null, summary: null };

  constructor() {
    // Auto-load whenever the project input changes.
    effect(() => {
      const p = this.projectName();
      if (p) this.refresh();
    });
  }

  refresh() {
    const p = this.projectName();
    if (!p) return;
    this.loading.set(true);
    this.docs.getSecurityOverview(p).subscribe({
      next: ov => {
        this.overview.set(ov);
        this.metaDraft = { ...ov.meta };
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); }
    });
  }

  ratingClass(r: string | null): 'good' | 'warn' | 'bad' | 'neutral' {
    if (!r) return 'neutral';
    const s = r.toLowerCase();
    if (s.includes('ok') || s.includes('good') || s.includes('baseline')) return 'good';
    if (s.includes('risk') || s.includes('bad') || s.includes('fail')) return 'bad';
    if (s.includes('review') || s.includes('warn') || s.includes('todo')) return 'warn';
    return 'neutral';
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try { return new Date(iso).toLocaleDateString('en-US'); } catch { return iso; }
  }

  openFile(rel: string) {
    this.editing.set(false);
    this.openedRel.set(rel);
    this.openedContent.set('');
    this.docs.getSecurityFile(this.projectName(), rel).subscribe({
      next: r => this.openedContent.set(r.content),
      error: () => this.openedContent.set('(failed to load)')
    });
  }

  closeFile() {
    this.openedRel.set(null);
    this.openedContent.set('');
    this.editing.set(false);
  }

  startEdit() {
    this.editorDraft = this.openedContent();
    this.editing.set(true);
  }

  cancelEdit() {
    this.editing.set(false);
    this.editorDraft = '';
  }

  saveEdit() {
    const rel = this.openedRel();
    if (!rel) return;
    this.docs.putSecurityFile(this.projectName(), rel, this.editorDraft).subscribe({
      next: () => {
        this.openedContent.set(this.editorDraft);
        this.editing.set(false);
        this.refresh();
      },
      error: () => this.editing.set(true)
    });
  }

  startNewFile() {
    this.newFileMode.set(true);
    this.newFileName = 'overview.md';
  }
  cancelNewFile() {
    this.newFileMode.set(false);
    this.newFileName = '';
  }
  createNewFile() {
    let name = (this.newFileName || '').trim();
    if (!name) return;
    if (!name.toLowerCase().endsWith('.md')) name += '.md';
    const seed = `# ${name.replace(/\.md$/i, '')}\n\nWrite the situation, the requirements, and any relevant history here.\n`;
    this.docs.putSecurityFile(this.projectName(), name, seed).subscribe({
      next: () => {
        this.newFileMode.set(false);
        this.newFileName = '';
        this.refresh();
        this.openFile(name);
      },
      error: () => this.newFileMode.set(true)
    });
  }

  saveMeta() {
    this.docs.putSecurityMeta(this.projectName(), this.metaDraft).subscribe({
      next: () => this.refresh(),
      error: () => this.refresh()
    });
  }
}

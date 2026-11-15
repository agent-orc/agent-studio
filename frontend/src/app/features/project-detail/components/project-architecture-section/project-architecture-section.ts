import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { ArchitectureOverview } from '../../../../models/project-docs.model';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

type AdrStatusClass = 'accepted' | 'superseded' | 'deprecated' | 'other';

/**
 * ADR status tones: an accepted decision is the live one; a superseded one
 * points at its replacement (info); a deprecated one is retired and stays
 * calm (R4); free-form statuses such as Proposed are neutral.
 */
const ADR_STATUS_TONES: Record<AdrStatusClass, StatusTone> = {
  accepted: 'success',
  superseded: 'info',
  deprecated: 'neutral',
  other: 'neutral',
};

/**
 * Project-level Architecture section: browse the project's
 * architecture-decisions.md, opening one entry at a time. Lighter
 * than security: read-only viewer with a simple "Comment" affordance
 * that captures local notes (not yet persisted — prototype hook
 * for the future chat-style interaction the user described).
 */
@Component({
  selector: 'app-project-architecture-section',
  standalone: true,
  imports: [MarkdownViewComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-architecture-section.html',
  styleUrl: './project-architecture-section.scss'
})
export class ProjectArchitectureSectionComponent {
  readonly projectName = input.required<string>();

  private readonly docs = inject(ProjectDocsService);

  readonly overview = signal<ArchitectureOverview | null>(null);
  readonly loading = signal(false);
  readonly openedId = signal<string | null>(null);
  readonly notes = signal<Record<string, string>>({});

  constructor() {
    effect(() => {
      const p = this.projectName();
      if (p) this.refresh();
    });
  }

  refresh() {
    const p = this.projectName();
    if (!p) return;
    this.loading.set(true);
    this.docs.getArchitectureOverview(p).subscribe({
      next: ov => {
        this.overview.set(ov);
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); }
    });
  }

  toggle(id: string) {
    this.openedId.update(curr => curr === id ? null : id);
  }

  statusClass(s: string): AdrStatusClass {
    const k = s.toLowerCase();
    if (k.includes('accept')) return 'accepted';
    if (k.includes('super')) return 'superseded';
    if (k.includes('depr')) return 'deprecated';
    return 'other';
  }

  statusTone(s: string): StatusTone {
    return ADR_STATUS_TONES[this.statusClass(s)];
  }

  /**
   * The raw status is free-form prose (e.g. "Superseded in part by
   * ADR-0007: …"). Long statuses are split into a compact keyword chip in
   * the row header plus a full-width wrapping note below, so the title
   * keeps the row width and never collapses to one word per line.
   */
  private static readonly LONG_STATUS_CHARS = 24;

  isLongStatus(s: string): boolean {
    return s.trim().length > ProjectArchitectureSectionComponent.LONG_STATUS_CHARS;
  }

  /** Short keyword for the inline chip when the full status is too long to sit inline. */
  statusKeyword(s: string): string {
    switch (this.statusClass(s)) {
      case 'accepted': return 'Accepted';
      case 'superseded': return 'Superseded';
      case 'deprecated': return 'Deprecated';
      default: return s.trim().split(/\s+/)[0] || s;
    }
  }

  /** Inline chip text: the full status when short, otherwise just the keyword. */
  inlineStatus(s: string): string {
    return this.isLongStatus(s) ? this.statusKeyword(s) : s.trim();
  }

  noteFor(id: string): string { return this.notes()[id] ?? ''; }

  onNoteInput(id: string, ev: Event) {
    const v = (ev.target as HTMLTextAreaElement).value;
    this.notes.update(n => ({ ...n, [id]: v }));
  }
}

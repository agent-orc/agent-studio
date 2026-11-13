import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, Injectable, effect, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { StudioTabStateService } from '../../studio-shell/services/studio-tab-state.service';

interface IssueStory { id: string; title: string; persona: string; role: string; importance: number;
  teaser: string | null; recommendedOption: string | null; deadlineUtc: string | null }
interface IssueSection { roleId: string; role: string; count: number; stories: IssueStory[]; teaser: string | null }
interface IssueWatchRow { id: string; group: string; label: string; state: string; importance: number; itemCount: number }
interface IssueTrendWeek { week: string; raised: number; accepted: number; tokensUsed: number;
  cardsDelivered: number; costPerAcceptedFinding: number | null; budgetUsePercent: number | null }
interface IssueArticle { scope: string; slug: string; revision: number; title: string;
  summary: string; persona: string; personaId: string | null; role: string }
interface IssueMonthlyReview { personaId: string; persona: string; roleId: string; value: {
  weightedAcceptedPer100kTokens: number | null; costPerAcceptedUsd: number | null; noisePercent: number | null;
  misses: { name: string; url: string }[]; useful: number; notUseful: number;
  suggestions: { reason: string }[] } }
interface WeeklyIssue { id: string; workspaceId: string; kind: string; masthead: string; coverageLine: string;
  periodStart: string; periodEnd: string; strapTotal: number; lead: IssueStory | null;
  leadParagraph: string | null; sideStories: IssueStory[]; decisionsNeeded: IssueStory[];
  operationsWatch: IssueWatchRow[]; sections: IssueSection[]; trends: IssueTrendWeek[];
  corrections: IssueStory[]; colophon: string[]; editorSkipped: boolean; articles: IssueArticle[];
  reviewMonth: string | null; monthlyReview: IssueMonthlyReview[] }

@Injectable({ providedIn: 'root' })
class WeeklyIssueSelection { readonly issueId = signal<string | null>(null); }

@Component({ selector: 'app-weekly-issue-view', standalone: true, imports: [DatePipe, DecimalPipe],
  templateUrl: './weekly-issue-view.html', styleUrl: './weekly-issue-view.scss',
  changeDetection: ChangeDetectionStrategy.OnPush })
export class WeeklyIssueViewComponent {
  readonly workspaceId = input.required<string>();
  readonly compact = input(false);
  private readonly http = inject(HttpClient);
  private readonly tabs = inject(StudioTabStateService);
  private readonly selection = inject(WeeklyIssueSelection);
  readonly issues = signal<WeeklyIssue[]>([]);
  readonly selected = signal<WeeklyIssue | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  constructor() {
    effect(() => { const id = this.workspaceId(); if (id) this.load(id); });
    effect(() => {
      if (this.compact()) return;
      const rows = this.issues(); const id = this.selection.issueId();
      this.selected.set(rows.find(issue => issue.id === id) ?? rows[0] ?? null);
    });
  }
  load(id = this.workspaceId()): void {
    this.loading.set(true); this.error.set('');
    this.http.get<WeeklyIssue[]>(`/api/workspaces/${encodeURIComponent(id)}/personas/issues`).subscribe({
      next: rows => { this.issues.set(rows); this.loading.set(false); },
      error: () => { this.error.set('Magazine issues could not be loaded.'); this.loading.set(false); },
    });
  }
  openMagazine(issue?: WeeklyIssue): void {
    this.selection.issueId.set(issue?.id ?? null);
    this.tabs.open({ kind: 'team', personaId: 'magazine' });
  }
  choose(issue: WeeklyIssue): void { this.selected.set(issue); }
  async exportIssue(issue: WeeklyIssue): Promise<void> {
    const path = `/api/workspaces/${encodeURIComponent(issue.workspaceId)}/personas/issues/${encodeURIComponent(issue.id)}/export`;
    try {
      const html = await firstValueFrom(this.http.get(path, { responseType: 'text' }));
      const url = URL.createObjectURL(new Blob([html], { type: 'text/html;charset=utf-8' }));
      const anchor = document.createElement('a'); anchor.href = url; anchor.download = `${issue.id}.html`; anchor.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch { this.error.set('Issue export failed.'); }
  }
}

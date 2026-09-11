import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { TaskCommitInfo } from '../../../../git';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatCompactDateTime, formatDateTime } from '../../../../../services/format.util';

interface SupersededRound {
  key: string;
  label: string;
  commits: TaskCommitInfo[];
}

@Component({
  selector: 'app-task-commit-rounds',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './task-commit-rounds.component.html',
  styleUrl: './task-commit-rounds.component.scss',
})
export class TaskCommitRoundsComponent {
  readonly commits = input.required<TaskCommitInfo[]>();
  readonly selectedSha = input<string | null>(null);
  readonly filesCount = input(0);
  readonly selectSha = output<string | null>();

  readonly collapsed = signal(readCollapsed());
  readonly activeCommits = computed(() =>
    this.commits().filter((commit) => !isSuperseded(commit)),
  );
  readonly supersededRounds = computed<SupersededRound[]>(() => buildSupersededRounds(this.commits()));
  readonly summary = computed(() => {
    if (this.selectedSha() === null && this.activeCommits().length > 1) {
      const files = this.filesCount();
      return `All ${this.activeCommits().length} commits · ${files} ${files === 1 ? 'file' : 'files'}`;
    }
    const selected = this.commits().find((commit) => commit.sha === this.selectedSha());
    return selected
      ? `${selected.shortSha} · ${selected.message.split('\n')[0]}`
      : `${this.activeCommits().length} current task commits`;
  });

  toggleCollapsed(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    writeCollapsed(next);
  }

  tooltip(entry: TaskCommitInfo, index: number, total: number): string {
    const base = `${index + 1}/${total} · ${entry.shortSha} · ${formatDateTime(entry.at)} · ${entry.message}`;
    const pushNote = this.pushStatusTooltip(entry);
    return pushNote ? `${base}\n${pushNote}` : base;
  }

  timestamp(entry: TaskCommitInfo): string {
    return formatCompactDateTime(entry.at);
  }

  /** Short push-backstop badge (AGT-2761): permanently skipped or backing off. Null for a normal push candidate. */
  pushStatusLabel(entry: TaskCommitInfo): string | null {
    switch (pushStatusKind(entry)) {
      case 'superseded': return 'not pushed';
      case 'rejected': return 'push rejected';
      case 'backing-off': return 'push retrying';
      default: return null;
    }
  }

  pushStatusKindOf(entry: TaskCommitInfo): string {
    return pushStatusKind(entry) ?? '';
  }

  private pushStatusTooltip(entry: TaskCommitInfo): string | null {
    switch (pushStatusKind(entry)) {
      case 'superseded':
        return 'Not reachable from the integrated result or the remote; the backstop will never push it.';
      case 'rejected':
        return `Rejected by the remote as non-fast-forward after ${entry.pushAttempts ?? 0} attempts; the backstop stopped retrying.${entry.pushError ? ` (${entry.pushError})` : ''}`;
      case 'backing-off':
        return `Rejected as non-fast-forward; retrying after ${entry.pushNextRetryAt ? formatDateTime(entry.pushNextRetryAt) : 'a backoff window'}.`;
      default:
        return null;
    }
  }
}

type PushStatusKind = 'superseded' | 'rejected' | 'backing-off';

function pushStatusKind(entry: TaskCommitInfo): PushStatusKind | null {
  if (entry.pushStatus === 'superseded') return 'superseded';
  if (entry.pushStatus === 'push-rejected') return 'rejected';
  if (entry.pushNextRetryAt) return 'backing-off';
  return null;
}

function buildSupersededRounds(commits: TaskCommitInfo[]): SupersededRound[] {
  const attemptIds: string[] = [];
  for (const commit of commits) {
    const attempt = commit.runAttemptId?.trim();
    if (attempt && !attemptIds.includes(attempt)) attemptIds.push(attempt);
  }

  const groups = new Map<string, {
    sourceAttempt: string | null;
    replacement: string;
    replacementKind: 'sha' | 'attempt';
    commits: TaskCommitInfo[];
  }>();
  commits.forEach((commit, index) => {
    const replacementSha = commit.supersededBySha?.trim();
    const replacementAttempt = commit.supersededByAttempt?.trim();
    const replacement = replacementSha || replacementAttempt;
    if (!replacement) return;
    const sourceAttempt = commit.runAttemptId?.trim() || null;
    const replacementKind = replacementSha ? 'sha' : 'attempt';
    const key = `${sourceAttempt ?? `legacy-${index + 1}`}|${replacementKind}|${replacement}`;
    const existing = groups.get(key);
    if (existing) existing.commits.push(commit);
    else groups.set(key, { sourceAttempt, replacement, replacementKind, commits: [commit] });
  });

  return [...groups.entries()].map(([key, group], groupIndex) => {
    const sourceIndex = group.sourceAttempt ? attemptIds.indexOf(group.sourceAttempt) : -1;
    const sourceRound = sourceIndex >= 0 ? sourceIndex + 1 : groupIndex + 1;
    const replacementLabel = group.replacementKind === 'sha'
      ? `SHA ${group.replacement.slice(0, 9)}`
      : replacementRoundLabel(commits, attemptIds, group.replacement, sourceRound);
    return {
      key,
      label: group.replacementKind === 'sha'
        ? `Round ${sourceRound}, mechanically replaced by ${replacementLabel}`
        : `Round ${sourceRound}, replaced by ${replacementLabel}`,
      commits: group.commits,
    };
  });
}

function replacementRoundLabel(
  commits: TaskCommitInfo[],
  attemptIds: string[],
  replacement: string,
  sourceRound: number,
): string {
  if (replacement === 'next-attempt') return 'next attempt';
  const replacementCommit = commits.find((commit) =>
    commit.runAttemptId === replacement
    || commit.resultSha === replacement
    || commit.sha === replacement);
  const replacementAttempt = replacementCommit?.runAttemptId ?? replacement;
  const replacementIndex = attemptIds.indexOf(replacementAttempt);
  return `round ${replacementIndex >= 0 ? replacementIndex + 1 : sourceRound + 1}`;
}

function isSuperseded(commit: TaskCommitInfo): boolean {
  return !!commit.supersededBySha?.trim() || !!commit.supersededByAttempt?.trim();
}

const COLLAPSED_KEY = 'taskboard.gitPane.commitGroupCollapsed';

function readCollapsed(): boolean {
  try {
    const stored = localStorage.getItem(COLLAPSED_KEY);
    return stored === null ? true : stored === '1';
  }
  catch { return true; }
}

function writeCollapsed(value: boolean): void {
  try { localStorage.setItem(COLLAPSED_KEY, value ? '1' : '0'); }
  catch { /* ignore quota / privacy-mode errors */ }
}

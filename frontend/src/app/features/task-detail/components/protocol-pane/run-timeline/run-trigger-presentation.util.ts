import type { TaskInfo } from '../../../../../models/task.model';
import type { RunRecord } from '../../../../../features/run-timeline';

function sourceField(run: RunRecord, key: string): string | null {
  const entry = run.triggerSource?.split(';').find((part) => part.startsWith(`${key}=`));
  return entry?.slice(key.length + 1).trim() || null;
}

function reviewContext(run: RunRecord): string | null {
  const review = sourceField(run, 'review');
  const aspects = sourceField(run, 'aspects')
    ?.split(',')
    .map((aspect) => aspect.replaceAll('-', ' ').replace(/\b\w/g, (letter) => letter.toUpperCase()))
    .join(', ');
  if (!review && !aspects) return null;
  return `: ${aspects || 'Review'}${review ? ` (${review})` : ''}`;
}

export function runTriggerLabel(run: RunRecord): string {
  const reason = run.triggerReason?.trim();
  const actor = run.triggeredBy?.replace(/^operator\s+/i, '').trim();
  switch (run.trigger) {
    case 'initial': return run.index === 1 ? 'Initial start' : 'Initial trigger recorded on a later run';
    case 'operator-continue': return `Operator continue${actor ? ` by ${actor}` : ''}${reason ? `: ${reason}` : ''}`;
    case 'review-finding': return `Review finding${reviewContext(run) ?? (reason ? `: ${reason}` : '')}`;
    case 'review-concern': return `Review concern${reviewContext(run) ?? (reason ? `: ${reason}` : '')}`;
    case 'integration-recovery': return `Integration recovery${reason ? `: ${reason}` : ''}`;
    case 'gate-failure': return `Gate failure${reason ? `: ${reason}` : ''}`;
    case 'timeout-continuation': return `Timeout continuation${reason ? `: ${reason}` : ''}`;
    case 'recovery-after-crash': return `Recovery after crash${reason ? `: ${reason}` : ''}`;
    case 'restart': return `Restart${reason ? `: ${reason}` : ''}`;
    case 'replan': return `Replan${reason ? `: ${reason}` : ''}`;
    case 'dependency-release': return `Dependency release${reason ? `: ${reason}` : ''}`;
    default: return 'Not recorded';
  }
}

export function runTriggerReportHref(run: RunRecord, job: TaskInfo | null): string | null {
  const review = sourceField(run, 'review');
  const failure = sourceField(run, 'failure');
  if (!job || (!review && !failure)) return null;
  const firstAspect = sourceField(run, 'aspects')?.split(',')[0]?.trim();
  const file = failure
    ? 'pipeline-execution.json'
    : review!.startsWith('local-review-') && firstAspect
      ? `aspect-${firstAspect}.json`
      : `remote-review-grade-${review!.replace(/[^A-Za-z0-9_.-]/g, '_')}.md`;
  return `/api/tasks/${encodeURIComponent(job.id)}/files/${encodeURIComponent(file)}?watchPath=${encodeURIComponent(job.watchPath)}`;
}

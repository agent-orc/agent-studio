import type { ChatEvent } from 'coding-agent-chat/core';
import type { WatchPathEntry } from '../../../../models/task.model';
import type { TaskService } from '../../../../services/task.service';

interface BugDirectiveInput {
  text: string;
  project: string;
  watchPaths: readonly WatchPathEntry[];
  jobService: TaskService;
  appendUser: (id: string, timestamp: string, text: string) => void;
  appendEvent: (event: ChatEvent) => void;
  addTarget: (eventId: string, jobId: string, watchPath: string) => void;
}

/** Lazy `/bug` workflow so task-creation code does not inflate the app entry bundle. */
export function handleBugDirective(input: BugDirectiveInput): void {
  const description = input.text.replace(/^\/bug\s*/, '').trim();
  const now = Date.now();
  input.appendUser(`bug-local:${now}`, new Date(now).toISOString(), input.text);

  if (!description) {
    input.appendEvent({
      id: `bug-err:empty:${now}`,
      kind: 'task',
      timestamp: new Date(now).toISOString(),
      severity: 'error',
      summary: 'Bug not filed: description is empty',
      detail: 'Add a description after `/bug`, e.g. `/bug Frontend chips overlap on narrow viewport`.'
    });
    return;
  }

  const watchPath = input.watchPaths.find(item => item.name === input.project)?.path;
  if (!watchPath) {
    input.appendEvent({
      id: `bug-err:no-watchpath:${now}`,
      kind: 'task',
      timestamp: new Date(now).toISOString(),
      severity: 'error',
      summary: 'Bug not filed: no watch path for this project',
      detail: `Could not resolve a watch path for project \`${input.project}\`. Check the workspace configuration.`
    });
    return;
  }

  const tags = parseBugHashtags(description);
  const firstLine = description.split('\n')[0].trim();
  const title = firstLine.length > 80 ? firstLine.slice(0, 77) + '...' : firstLine;
  input.jobService.createJob({
    title,
    agent: 'claude',
    watchPath,
    promptMarkdown: `${description}\n\n---\n\nReported via /bug from project chat`,
    targetState: '0-backlog',
    taskType: 'bug',
    tags: tags.length > 0 ? tags : undefined
  }).subscribe({
    next: response => {
      const eventId = `bug-ok:${response.id}`;
      input.addTarget(eventId, response.id, watchPath);
      const tagSuffix = tags.length > 0
        ? `\n\nTags: ${tags.map(tag => '`' + tag + '`').join(' ')}`
        : '';
      input.appendEvent({
        id: eventId,
        kind: 'task',
        timestamp: new Date().toISOString(),
        summary: `Bug filed in 0-backlog: ${title}`,
        detail:
          `**Lane:** \`0-backlog\`  \n` +
          `**Task type:** \`bug\`  \n` +
          `**Job ID:** \`${response.id}\`${tagSuffix}\n\n` +
          'The new task is in triage. Open the detail panel to refine the prompt before promoting it to `2-ready`.',
        actionLabel: 'Open task'
      });
      input.jobService.refresh(true);
    },
    error: error => {
      const message = error?.error?.error
        || (typeof error?.error === 'string' ? error.error : null)
        || error?.message
        || 'Failed to file bug';
      input.appendEvent({
        id: `bug-err:${Date.now()}`,
        kind: 'task',
        timestamp: new Date().toISOString(),
        severity: 'error',
        summary: `Bug not filed: ${title || '(empty title)'}`,
        detail: `**Error:** ${message}`
      });
    }
  });
}

export function parseBugHashtags(description: string): string[] {
  const found: string[] = [];
  for (const line of description.split('\n')) {
    const trimmed = line.trim();
    if (!/^#[A-Za-z]/.test(trimmed)) continue;
    for (const match of trimmed.match(/#[A-Za-z][\w-]*/g) ?? []) {
      const tag = match.substring(1);
      if (!found.includes(tag)) found.push(tag);
    }
  }
  return found;
}

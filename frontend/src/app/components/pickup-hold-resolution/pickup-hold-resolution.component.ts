import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import type { Observable } from 'rxjs';
import { CLI_TYPES, TaskState, type CliType, type PickupHoldResolution, type TaskInfo } from '../../models/task.model';
import {
  PROJECT_SETTING_KEYS, REMEDY_API_ACTIONS, remedyLinkLabel, type RemedyAction, type RemedyApiAction,
} from '../../models/remedy-action.model';
import { cliTypeLabel } from '../../services/format.util';
import { RemedyNavigationService } from '../../services/remedy-navigation.service';
import { TaskService } from '../../services/task.service';
import { RemoteHostsService } from '../../features/remote-hosts';

/** How one way out renders: the button that calls its API, a picker plus button, or a link. */
type ResolutionControl = 'api' | 'runner-picker' | 'cli-picker' | 'link' | 'none';

/** Button text where the label names the decision rather than the click ("Wait out the cooldown"). */
const API_BUTTON_TEXT: Partial<Record<RemedyApiAction, string>> = {
  'set-execution-runner': 'Route',
  'set-cli-type': 'Assign',
  'start-run': 'Start the run now',
  'run-intake': 'Run intake now',
  'move-to-escalated': 'Move to escalated',
};

const KNOWN_API_ACTIONS = new Set<string>(Object.values(REMEDY_API_ACTIONS));

/**
 * AGT-3580 - one way out of a pickup hold, rendered from its typed action. The
 * operator gets a button that calls the existing API route, a picker for the
 * runner or CLI the route needs, or a link to the owning setting or task; the
 * detail stays prose. Nothing here is applied without the operator's click.
 */
@Component({
  selector: 'app-pickup-hold-resolution',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pickup-hold-resolution.component.html',
  styleUrl: './pickup-hold-resolution.component.scss',
})
export class PickupHoldResolutionComponent implements OnInit {
  private readonly tasks = inject(TaskService, { optional: true });
  private readonly hostRegistry = inject(RemoteHostsService, { optional: true });
  private readonly navigation = inject(RemedyNavigationService, { optional: true });
  readonly resolution = input.required<PickupHoldResolution>();
  readonly task = input.required<TaskInfo>();

  readonly pending = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly selectedRunner = signal('');
  readonly selectedCli = signal<CliType>(CLI_TYPES[0]);
  readonly cliOptions = CLI_TYPES.map(cli => ({ id: cli, label: cliTypeLabel(cli) }));

  readonly action = computed<RemedyAction | null>(() => this.resolution().action ?? null);
  readonly control = computed<ResolutionControl>(() => controlFor(this.action(), this.resolution(), this.navigation));
  readonly buttonText = computed(() =>
    API_BUTTON_TEXT[this.action()?.target as RemedyApiAction] ?? this.resolution().label);
  /** The label is the button itself when they read the same; otherwise it stays a heading. */
  readonly labelIsButton = computed(() => this.control() === 'api' && this.buttonText() === this.resolution().label);
  readonly linkText = computed(() => {
    const action = this.action();
    if (this.resolution().kind === 'route-to-remote-runner' && action?.kind === 'setting')
      return 'Open Project Settings';
    return this.control() === 'link' && action ? remedyLinkLabel(action) : null;
  });
  readonly runners = computed(() => (this.hostRegistry?.hosts() ?? [])
    .filter(host => host.role === 'remote' && host.status !== 'retired'));
  /** The execution-placement setting, offered beside the runner picker. */
  readonly placementSetting = computed<RemedyAction | null>(() => {
    const project = this.action()?.project ?? this.task().projectName;
    return project ? { kind: 'setting', target: PROJECT_SETTING_KEYS.executionRunner, project } : null;
  });

  ngOnInit(): void {
    if (this.control() !== 'runner-picker') return;
    this.hostRegistry?.ensureLoaded?.();
    this.selectedRunner.set(this.resolution().targetKey?.trim() ?? '');
  }

  openLink(action: RemedyAction | null = this.action()): void {
    this.navigation?.open(action);
  }

  apply(): void {
    const action = this.action();
    if (this.pending() || !action || !this.tasks) return;
    const request = this.request(action.target, this.tasks);
    if (request) this.run(request);
  }

  private request(target: string, tasks: TaskService): Observable<unknown> | null {
    const resolution = this.resolution();
    const task = this.task();
    const targetKey = resolution.targetKey?.trim() ?? '';
    switch (target) {
      case 'edit-waits-on': {
        if (resolution.kind === 'drop-cycle-edge') {
          const source = (this.action()?.taskKey ?? resolution.sourceKey ?? '').trim();
          return tasks.editTaskWaitsOn(source, {
            remove: [targetKey], reason: `Operator dropped cycle edge ${source} -> ${targetKey}.`, requireCycleEdge: true,
          });
        }
        if (resolution.kind === 'repoint-dependency') {
          const successor = window.prompt(`Successor task key for ${targetKey}`)?.trim();
          if (!successor) return null;
          return tasks.editTaskWaitsOn(task.id, {
            remove: [targetKey], add: [successor],
            reason: `Operator re-pointed unsatisfiable dependency ${targetKey} to ${successor}.`,
          }, task.watchPath);
        }
        const what = resolution.kind === 'drop-release-gate' ? 'release gate' : 'unsatisfiable dependency';
        return tasks.editTaskWaitsOn(task.id, {
          remove: [targetKey], reason: `Operator dropped ${what} ${targetKey}.`,
        }, task.watchPath);
      }
      case 'archive-task':
        return tasks.moveJob(task.id, TaskState.Archive, task.watchPath, undefined,
          'Operator archived a card with an unsatisfiable dependency.');
      case 'release-task': {
        const key = this.action()?.taskKey ?? targetKey;
        if (!window.confirm(`Release ${key}? ${resolution.detail}`)) return null;
        return tasks.setTaskReleased(key, true);
      }
      case 'clear-dispatch-rejection':
        return tasks.clearDispatchRejection(task.id, task.watchPath);
      case 'start-run':
        return tasks.startJob(task.id, task.watchPath);
      case 'set-cli-type':
        return tasks.setJobCliType(task.id, this.selectedCli(), task.watchPath);
      case 'run-intake':
        return tasks.runTaskIntake(task.id, task.watchPath);
      case 'move-to-escalated':
        return tasks.moveJob(task.id, TaskState.Escalated, task.watchPath, undefined,
          'Operator routed a human-decision marker to the escalated lane.');
      case 'set-execution-runner': {
        const runner = this.selectedRunner().trim();
        const project = this.action()?.project ?? task.projectName;
        return runner && project ? tasks.setProjectExecutionLocation(project, runner) : null;
      }
      default:
        return null;
    }
  }

  private run(request: Observable<unknown>): void {
    this.pending.set(true);
    this.error.set(null);
    this.notice.set(null);
    request.subscribe({
      next: response => {
        this.pending.set(false);
        const message = response && typeof response === 'object' && 'message' in response ? response.message : null;
        this.notice.set(typeof message === 'string' ? message : `${this.resolution().label}: done.`);
      },
      error: () => {
        this.pending.set(false);
        this.error.set(`${this.resolution().label} could not be applied.`);
      },
    });
  }
}

/** The control a resolution earns; a malformed or unknown action stays explanatory text. */
export function controlFor(
  action: RemedyAction | null,
  resolution: PickupHoldResolution,
  navigation: Pick<RemedyNavigationService, 'canOpen'> | null,
): ResolutionControl {
  if (!action) return 'none';
  if (action.kind === 'route' || action.kind === 'setting') return navigation?.canOpen(action) ? 'link' : 'none';
  if (action.kind !== 'api' || !KNOWN_API_ACTIONS.has(action.target)) return 'none';
  const target = resolution.targetKey?.trim();
  switch (action.target) {
    case 'set-execution-runner': return 'runner-picker';
    case 'set-cli-type': return 'cli-picker';
    case 'edit-waits-on':
      return resolution.kind === 'drop-cycle-edge'
        ? (target && (action.taskKey ?? resolution.sourceKey)?.trim() ? 'api' : 'none')
        : (target ? 'api' : 'none');
    case 'release-task': return (action.taskKey ?? target) ? 'api' : 'none';
    default: return 'api';
  }
}

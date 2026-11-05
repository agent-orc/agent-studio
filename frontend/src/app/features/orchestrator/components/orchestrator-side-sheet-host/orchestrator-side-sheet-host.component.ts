import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { WatchPathEntry } from '../../../../models/task.model';
import type { PageContext } from '../../../../models/page-context.model';
import type { ComposerLocationContext } from '../../composer-location-context';
import { OrchestratorSideSheetComponent } from '../orchestrator-side-sheet/orchestrator-side-sheet.component';

/** Keeps the projected shell slot while the chat implementation loads on first open. */
@Component({
  selector: 'app-orchestrator-side-sheet-host',
  standalone: true,
  imports: [OrchestratorSideSheetComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-side-sheet-host.component.html',
  styleUrl: './orchestrator-side-sheet-host.component.scss',
})
export class OrchestratorSideSheetHostComponent {
  readonly open = input(false);
  readonly legacy = input(false);
  readonly projects = input<string[]>([]);
  readonly preferredProject = input<string | null>(null);
  readonly requestedProject = input<string | null>(null);
  readonly projectEntryRevision = input(0);
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly pageContext = input<PageContext | null>(null);
  readonly activeJobId = input<string | null>(null);
  readonly activeJobTitle = input<string | null>(null);
  readonly activeWatchPath = input<string | null>(null);
  readonly activeJobKey = input<string | null>(null);
  readonly activeJobState = input<string | null>(null);
  readonly activeRun = input<{ model: string | null; startedAt: string | null } | null>(null);
  readonly composerContext = input<ComposerLocationContext | null>(null);

  readonly openVerboseDebug = output<{ jobId: string; watchPath: string; jobTitle: string | null }>();
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();
  readonly navigateToContext = output<string>();
  readonly openSettings = output<void>();
}

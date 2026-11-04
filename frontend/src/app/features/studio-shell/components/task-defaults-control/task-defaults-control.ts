import { ChangeDetectionStrategy, Component, OnInit, ViewEncapsulation, inject, output, signal } from '@angular/core';
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import { ClientDefaultsService } from '../../../../services/client-defaults.service';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';

const CLI_KEY = 'defaultCliType';
const MODEL_KEY = 'defaultModel:';
const THINKING_KEY = 'defaultThinkingLevel:';

/** Current defaults for creating a task, directly reachable from the header. */
@Component({
  selector: 'app-task-defaults-control', standalone: true, imports: [CliModelSelectorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './task-defaults-control.html',
  styleUrl: './task-defaults-control.scss',
})
export class TaskDefaultsControlComponent implements OnInit {
  private readonly defaults = inject(ClientDefaultsService);
  readonly cli = signal<CliType>(this.readCli());
  readonly model = signal(this.readModel(this.cli()));
  readonly thinking = signal(this.readThinking(this.cli()));
  readonly cliChange = output<CliType>();
  readonly modelChange = output<{ cliType: CliType; model: string; thinkingLevel: string | null }>();

  ngOnInit(): void {
    void this.defaults.hydrate().then(() => {
      const cli = this.readCli();
      this.cli.set(cli); this.model.set(this.readModel(cli)); this.thinking.set(this.readThinking(cli));
    });
  }

  commit(change: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    const previous = this.cli();
    if (change.cliType !== previous) {
      this.cli.set(change.cliType);
      localStorage.setItem(CLI_KEY, change.cliType);
      void this.defaults.pushDefaultCli(change.cliType);
      this.cliChange.emit(change.cliType);
    }
    if (change.cliType !== previous || change.model !== this.model() || change.thinkingLevel !== this.thinking()) {
      this.model.set(change.model); this.thinking.set(change.thinkingLevel);
      if (change.model) localStorage.setItem(MODEL_KEY + change.cliType, change.model);
      else localStorage.removeItem(MODEL_KEY + change.cliType);
      if (change.thinkingLevel) localStorage.setItem(THINKING_KEY + change.cliType, change.thinkingLevel);
      else localStorage.removeItem(THINKING_KEY + change.cliType);
      void this.defaults.pushDefaultModel(change.model);
      void this.defaults.pushDefaultThinkingLevel(change.thinkingLevel);
      this.modelChange.emit(change);
    }
  }

  private readCli(): CliType {
    const stored = localStorage.getItem(CLI_KEY) as CliType | null;
    return stored && (CLI_TYPES as string[]).includes(stored) ? stored : 'claude';
  }
  private readModel(cli: CliType): string { return localStorage.getItem(MODEL_KEY + cli) ?? ''; }
  private readThinking(cli: CliType): string | null { return localStorage.getItem(THINKING_KEY + cli); }
}

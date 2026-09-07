import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { CliModelSelectorComponent } from '../../../../../components/cli-model-selector';
import { ModelMigrationOfferComponent } from '../../../../../components/model-migration-offer';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { CLI_TYPES, type CliType } from '../../../../../models/task.model';
import type { PipelineAdminRow } from '../pipeline-config.util';

export interface PipelineAgentSelection {
  cliType: CliType;
  model: string;
  thinkingLevel: string | null;
}

@Component({
  selector: 'app-pipeline-model-setting',
  standalone: true,
  imports: [CliModelSelectorComponent, ModelMigrationOfferComponent, TooltipDirective],
  templateUrl: './pipeline-model-setting.component.html',
  styleUrl: './pipeline-model-setting.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PipelineModelSettingComponent {
  readonly step = input.required<PipelineAdminRow>();
  readonly sourceLabel = input.required<string>();
  readonly busy = input(false);
  readonly commitSelection = output<PipelineAgentSelection>();
  readonly resetRequested = output<void>();
  readonly migrationRequested = output<string>();

  asCliType(value: string | null | undefined): CliType | null {
    return value && (CLI_TYPES as readonly string[]).includes(value) ? value as CliType : null;
  }
}

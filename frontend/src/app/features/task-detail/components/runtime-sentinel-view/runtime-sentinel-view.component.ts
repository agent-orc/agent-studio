import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { AspectSentinelStatus, ParsedRuntimeSentinels, ParsedTerminalSentinel } from '../runtime-sentinel.parser';
import { terminalSentinelLabel } from '../runtime-sentinel.parser';

const ASPECT_TONES: Readonly<Record<AspectSentinelStatus, StatusTone>> = {
  pass: 'success',
  concerns: 'warning',
  block: 'danger',
};

const TERMINAL_TONES: Readonly<Record<ParsedTerminalSentinel['kind'], StatusTone>> = {
  done: 'success',
  noop: 'neutral',
  blocked: 'danger',
  'needs-input': 'warning',
};

/** Tone of an aspect verdict sentinel; unknown statuses stay neutral. */
export function aspectSentinelTone(status: string): StatusTone {
  return ASPECT_TONES[status as AspectSentinelStatus] ?? 'neutral';
}

/** Tone of a terminal sentinel; unknown kinds stay neutral. */
export function terminalSentinelTone(kind: string): StatusTone {
  return TERMINAL_TONES[kind as ParsedTerminalSentinel['kind']] ?? 'neutral';
}

@Component({
  selector: 'app-runtime-sentinel-view',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './runtime-sentinel-view.component.html',
  styleUrl: './runtime-sentinel-view.component.scss',
})
export class RuntimeSentinelViewComponent {
  readonly sentinels = input.required<ParsedRuntimeSentinels>();
  readonly aspectTone = aspectSentinelTone;
  readonly terminalTone = terminalSentinelTone;

  terminalText(terminal: ParsedTerminalSentinel): string {
    const label = terminalSentinelLabel(terminal);
    return terminal.detail ? `${label} · ${terminal.detail}` : label;
  }
}

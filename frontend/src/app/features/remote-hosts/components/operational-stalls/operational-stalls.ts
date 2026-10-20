import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { OrchestratorFeedStore } from '../../../orchestrator';

@Component({
  selector: 'app-operational-stalls',
  standalone: true,
  templateUrl: './operational-stalls.html',
  styleUrl: './operational-stalls.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationalStallsComponent {
  private readonly feed = inject(OrchestratorFeedStore);
  readonly cases = computed(() => this.feed.openStalls().filter(item =>
    item.subject.startsWith('host:') || item.subject.startsWith('container:')));

  subjectLabel(subject: string): string {
    const [kind, host, service] = subject.split(':');
    return kind === 'container' ? `${service} on ${host}` : host;
  }

  timeLabel(value: string): string {
    return new Intl.DateTimeFormat('en-GB', {
      dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC',
    }).format(new Date(value)) + ' UTC';
  }
}

import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RemoteHostsService } from '../../services/remote-hosts.service';

@Component({
  selector: 'app-runner-infrastructure-failures',
  standalone: true,
  templateUrl: './runner-infrastructure-failures.html',
  styleUrl: './runner-infrastructure-failures.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunnerInfrastructureFailuresComponent {
  readonly failures = inject(RemoteHostsService).runnerInfrastructureFailures;
}

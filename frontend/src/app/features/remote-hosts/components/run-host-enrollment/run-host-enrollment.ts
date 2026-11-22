import { ChangeDetectionStrategy, Component, OnInit, inject, output, signal } from '@angular/core';
import { AccessService, runHostCommand } from '../../../access';

/**
 * F4: the Hosts wizard's run host path. It issues the same runner.deploy code
 * as Settings > Access, bound to the chosen projects, and prints the host
 * commands that enroll the host and start its deploy service.
 */
@Component({
  selector: 'app-run-host-enrollment',
  standalone: true,
  templateUrl: './run-host-enrollment.html',
  styleUrl: './run-host-enrollment.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunHostEnrollmentComponent implements OnInit {
  readonly access = inject(AccessService);
  readonly done = output<void>();
  readonly name = signal('run-host-1');
  readonly selected = signal<readonly string[]>([]);
  readonly code = signal<string | null>(null);
  readonly command = signal<string | null>(null);

  ngOnInit(): void {
    if (this.access.state() === 'idle') void this.access.load();
  }

  toggle(project: string): void {
    const current = this.selected();
    this.selected.set(current.includes(project) ? current.filter(item => item !== project) : [...current, project]);
  }

  async create(): Promise<void> {
    const name = this.name().trim();
    if (!name || !this.selected().length || this.access.busy()) return;
    const created = await this.access.createEnrollment(name, 'deploy', this.selected());
    if (!created) return;
    this.code.set(created.enrollmentCode);
    this.command.set(runHostCommand(location.origin, created.enrollmentCode));
  }
}

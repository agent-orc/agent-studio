import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { formatDisk, type HostFilesystemUsage } from '../../models/remote-host.model';

@Component({
  selector: 'app-host-filesystem-list',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './host-filesystem-list.html',
  styleUrl: './host-filesystem-list.scss',
})
export class HostFilesystemListComponent {
  readonly filesystems = input<readonly HostFilesystemUsage[]>([]);
  readonly rows = computed(() => this.filesystems().map(disk => ({
    ...disk,
    free: formatDisk(disk.freeBytes / 1073741824),
    total: formatDisk(disk.totalBytes / 1073741824),
    percent: disk.totalBytes > 0 ? Math.round(100 * disk.freeBytes / disk.totalBytes) : 0,
    consumers: Object.entries(disk.ownedBytes ?? {})
      .sort((a, b) => b[1] - a[1])
      .map(([name, bytes]) => `${name} ${formatDisk(bytes / 1073741824)}`).join(', '),
  })));
}

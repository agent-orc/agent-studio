import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { TaskService } from '../../../services/task.service';

@Injectable()
export class ChatMetadataPreferenceService implements OnDestroy {
  private readonly tasks = inject(TaskService);
  private readonly userOverride = signal<boolean | null>(
    typeof localStorage === 'undefined' ? null :
      localStorage.getItem('atp.chat.metadata.enabled.v1') === null ? null :
        localStorage.getItem('atp.chat.metadata.enabled.v1') === '1');
  private readonly projectDefault = signal(true);
  readonly enabled = computed(() => this.userOverride() ?? this.projectDefault());
  private activeProject: string | null = null;
  private subscription: Subscription | null = null;

  selectProject(project: string | null): void {
    if (project === this.activeProject) return;
    this.activeProject = project;
    this.subscription?.unsubscribe();
    this.projectDefault.set(true);
    if (!project) return;
    this.subscription = this.tasks.getProjectChatMetadata(project).subscribe({
      next: setting => this.projectDefault.set(setting.chatMetadataEnabled ?? true),
    });
  }

  toggle(): void {
    const enabled = !this.enabled();
    this.userOverride.set(enabled);
    localStorage.setItem('atp.chat.metadata.enabled.v1', enabled ? '1' : '0');
  }

  ngOnDestroy(): void { this.subscription?.unsubscribe(); }
}

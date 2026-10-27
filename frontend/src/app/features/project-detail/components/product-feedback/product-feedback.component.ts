import { ChangeDetectionStrategy, Component, HostListener, computed, inject, input, signal } from '@angular/core';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import { laneName } from '../../../../models/lane-presentation';
import type { TaskScreenshot } from '../../../screenshots';
import { TaskService } from '../../../../services/task.service';
import { OrchestratorPanelStateService } from '../../../orchestrator/state/orchestrator-panel-state.service';
import { StudioPanelStateService } from '../../../studio-shell/services/studio-panel-state.service';

export function productStatus(task: TaskInfo): { label: string; next: string } {
  switch (task.state) {
    case TaskState.Progress: return { label: 'In progress', next: 'Studio is making the change.' };
    case TaskState.AutoReview: return { label: 'Being checked', next: 'Studio is checking the delivery.' };
    case TaskState.HumanReview: return { label: 'Ready to look at', next: 'Review the preview, then accept or leave feedback.' };
    case TaskState.Completed: return { label: laneName(TaskState.Completed), next: 'A release approver decides when it goes live.' };
    default: return { label: 'Waiting to start', next: 'Studio will pick up this change.' };
  }
}

interface Point { x: number; y: number }

@Component({
  selector: 'app-product-feedback',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './product-feedback.component.html',
  styleUrl: './product-feedback.component.scss',
})
export class ProductFeedbackComponent {
  readonly projectName = input.required<string>();
  readonly watchPath = input<string | null>(null);
  readonly previewAddress = input<string | null>(null);
  private readonly tasks = inject(TaskService);
  private readonly chat = inject(OrchestratorPanelStateService);
  private readonly panels = inject(StudioPanelStateService);
  readonly cards = computed(() => this.tasks.jobs().filter(job => job.projectName === this.projectName()
    && job.state !== TaskState.Archive));
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed(() => this.cards().find(card => card.id === this.selectedId()) ?? null);
  readonly status = computed(() => this.selected() ? productStatus(this.selected()!) : null);
  readonly screenshots = signal<TaskScreenshot[]>([]);
  readonly beforeShot = computed(() => [...this.screenshots()].reverse().find(shot => /\bbefore\b/i.test(shot.caption)) ?? null);
  readonly afterShot = computed(() => [...this.screenshots()].reverse().find(shot => /\bafter\b/i.test(shot.caption)) ?? null);
  readonly currentShot = signal<TaskScreenshot | null>(null);
  readonly comment = signal('');
  readonly selector = signal('');
  readonly message = signal<string | null>(null);
  readonly busy = signal(false);
  readonly feedbackShotUrls = signal<string[] | null>(null);
  readonly draftStep = signal<'closed' | 'wish' | 'location' | 'example' | 'confirm'>('closed');
  readonly wish = signal('');
  readonly location = signal('');
  readonly example = signal('');
  readonly box = signal<{ x: number; y: number; width: number; height: number } | null>(null);
  private start: Point | null = null;

  constructor() {
    this.collapseNavigationOnPhone();
  }

  @HostListener('window:resize')
  collapseNavigationOnPhone(): void {
    if (typeof window !== 'undefined' && window.innerWidth <= 600 && this.panels.visible())
      this.panels.setVisible(false);
  }

  openChat(): void { this.chat.setOpen(true); }

  startChange(): void { this.draftStep.set('wish'); }

  nextDraft(): void {
    switch (this.draftStep()) {
      case 'wish':
        if (this.wish().trim()) this.draftStep.set('location');
        break;
      case 'location':
        if (this.location().trim()) this.draftStep.set('example');
        break;
      case 'example': this.draftStep.set('confirm'); break;
    }
  }

  confirmChange(): void {
    const path = this.watchPath();
    if (!path || !this.wish().trim() || !this.location().trim() || this.busy()) return;
    const title = this.wish().trim().slice(0, 100);
    const prompt = [
      `Change request: ${this.wish().trim()}`,
      `Where: ${this.location().trim()}`,
      `Preview address: ${this.previewAddress() ?? 'not configured'}`,
      this.example().trim() ? `Example or reference: ${this.example().trim()}` : '',
      'Capture screenshots of every changed page before and after the delivery. Save them in results/ and name the page and state in each caption.',
      'Use the project screenshot-critique workflow to check the changed pages against accepted design references.',
      'Keep the implementation focused on the requested visible change.',
    ].filter(Boolean).join('\n\n');
    this.busy.set(true);
    this.tasks.createJob({
      title, watchPath: path, agent: 'codex', cliType: 'codex',
      targetState: TaskState.Ready, taskType: 'feature', promptMarkdown: prompt,
    }).subscribe({
      next: result => {
        this.busy.set(false);
        this.draftStep.set('closed');
        this.selectedId.set(result.id);
        this.tasks.refresh(true);
        this.message.set('Change created. Studio will start it when the project is ready.');
      },
      error: () => {
        this.busy.set(false);
        this.message.set('The change could not be created. Please try again.');
      },
    });
  }

  selectCard(id: string): void {
    if (id !== this.selectedId()) this.feedbackShotUrls.set(null);
    this.selectedId.set(id || null);
    this.screenshots.set([]);
    this.currentShot.set(null);
    this.box.set(null);
    this.message.set(null);
    this.loadScreenshots();
  }

  refreshScreenshots(): void {
    this.loadScreenshots();
  }

  private loadScreenshots(): void {
    const card = this.selected();
    if (!card) return;
    this.tasks.getJobScreenshots(card.id, card.watchPath).subscribe({
      next: result => {
        if (this.selectedId() !== card.id) return;
        const shots = result.screenshots ?? [];
        this.screenshots.set(shots);
        this.currentShot.set(shots.at(-1) ?? null);
        const previous = this.feedbackShotUrls();
        if (previous && shots.some(shot => !previous.includes(shot.url)))
          this.feedbackShotUrls.set(null);
      },
      error: () => this.message.set('Screenshots could not be loaded. Try again later.'),
    });
  }

  selectShot(url: string): void {
    this.currentShot.set(this.screenshots().find(shot => shot.url === url) ?? null);
    this.box.set(null);
  }

  private point(event: PointerEvent, image: HTMLImageElement): Point {
    const bounds = image.getBoundingClientRect();
    return {
      x: Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width)),
      y: Math.max(0, Math.min(1, (event.clientY - bounds.top) / bounds.height)),
    };
  }

  startBox(event: PointerEvent): void {
    const image = event.currentTarget as HTMLImageElement;
    this.start = this.point(event, image);
    image.setPointerCapture(event.pointerId);
    this.box.set({ x: this.start.x, y: this.start.y, width: 0, height: 0 });
  }

  moveBox(event: PointerEvent): void {
    if (!this.start) return;
    const end = this.point(event, event.currentTarget as HTMLImageElement);
    this.box.set({
      x: Math.min(this.start.x, end.x), y: Math.min(this.start.y, end.y),
      width: Math.abs(end.x - this.start.x), height: Math.abs(end.y - this.start.y),
    });
  }

  endBox(event: PointerEvent): void {
    this.moveBox(event);
    this.start = null;
  }

  async send(image: HTMLImageElement): Promise<void> {
    const card = this.selected();
    const address = this.previewAddress();
    const box = this.box();
    if (!card || !address || !box || !this.comment().trim()) {
      this.message.set('Choose an area and write what should change.');
      return;
    }
    if (!image.naturalWidth || !image.naturalHeight) {
      this.message.set('The screenshot is still loading.');
      return;
    }
    this.busy.set(true);
    this.message.set(null);
    try {
      const canvas = document.createElement('canvas');
      const x = Math.floor(box.x * image.naturalWidth);
      const y = Math.floor(box.y * image.naturalHeight);
      const width = Math.max(1, Math.ceil(box.width * image.naturalWidth));
      const height = Math.max(1, Math.ceil(box.height * image.naturalHeight));
      canvas.width = width;
      canvas.height = height;
      canvas.getContext('2d')?.drawImage(image, x, y, width, height, 0, 0, width, height);
      const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/png'));
      if (!blob) throw new Error('The selected area could not be captured.');
      const data = new FormData();
      data.set('crop', blob, 'preview-feedback.png');
      data.set('comment', this.comment().trim());
      data.set('previewAddress', address);
      data.set('selector', this.selector().trim());
      this.tasks.sendPreviewFeedback(card.id, card.watchPath, data).subscribe({
        next: () => {
          this.busy.set(false);
          this.feedbackShotUrls.set(this.screenshots().map(shot => shot.url));
          this.comment.set('');
          this.message.set('Feedback sent. Studio will update this change.');
        },
        error: () => {
          this.busy.set(false);
          this.message.set('Feedback could not be sent. Please try again.');
        },
      });
    } catch {
      this.busy.set(false);
      this.message.set('The selected area could not be captured.');
    }
  }

  accept(): void {
    const card = this.selected();
    if (!card || card.state !== TaskState.HumanReview || this.busy() || this.feedbackShotUrls()) return;
    this.busy.set(true);
    this.tasks.moveJob(card.id, TaskState.Completed, card.watchPath).subscribe({
      next: () => {
        this.busy.set(false);
        this.message.set('Accepted. A release approver decides when it goes live.');
      },
      error: () => {
        this.busy.set(false);
        this.message.set('Acceptance could not be recorded. Please try again.');
      },
    });
  }
}

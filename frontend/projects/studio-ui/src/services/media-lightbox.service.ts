import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';

/**
 * App-wide image lightbox / enlarge view.
 *
 * Markdown surfaces (task description, activity-log, chat, info-button,
 * results) all render images inline through their own renderer; clicking
 * an image should open a single shared overlay that respects the modal
 * stack (Escape, focus trap, etc.) instead of every surface re-inventing
 * its own zoom widget.
 *
 * Two entry points:
 *   inject(MediaLightboxService).open({ src, alt });            // single image
 *   inject(MediaLightboxService).openGallery({ images, index }); // paged set
 *
 * The gallery form lets a surface hand over every image it rendered plus
 * the one that was clicked, so the lightbox can page Prev/Next with the
 * arrow keys instead of the keys leaking up to the task-detail triage
 * handler (which would switch tasks). `open()` is just a one-element
 * gallery so the single-image callers (TipTap description editor, etc.)
 * keep working unchanged.
 *
 * The lightbox component (`<app-media-lightbox>`) mounts in the app shell
 * on first open. It binds `active()` to its template and calls `close()` on
 * dismiss. The service owns the modal-stack registration so any caller
 * gets correct Escape ordering for free.
 */
export interface MediaLightboxRequest {
  readonly src: string;
  readonly alt?: string | null;
  readonly actions?: readonly MediaLightboxActionRequest[];
}

export interface MediaLightboxGalleryRequest {
  readonly images: readonly MediaLightboxRequest[];
  /** Index of the image that was clicked; clamped into range. */
  readonly index?: number;
}

export interface MediaLightboxActionRequest {
  readonly id: string;
  readonly label: string;
  readonly tooltip?: string | null;
  readonly run: () => void | Promise<void>;
}

export interface MediaLightboxAction extends MediaLightboxActionRequest {
  readonly tooltip: string;
}

export interface MediaLightboxImage {
  readonly src: string;
  readonly alt: string;
  readonly actions: readonly MediaLightboxAction[];
}

/** Backwards-compatible shape for the current image. */
export type MediaLightboxState = MediaLightboxImage;

@Injectable({ providedIn: 'root' })
export class MediaLightboxService {
  private readonly images = signal<MediaLightboxImage[]>([]);
  private readonly cursor = signal(0);

  constructor() {
    // The overlay component is deferred. Capture arrows as soon as the service
    // opens so a key pressed before the component mounts cannot reach task paging.
    if (typeof document === 'undefined') return;
    const controller = new AbortController();
    document.addEventListener('keydown', event => {
      if (this.active() === null || event.metaKey || event.ctrlKey || event.altKey) return;
      if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return;
      event.preventDefault();
      event.stopPropagation();
      if (event.key === 'ArrowRight') this.next();
      else this.prev();
    }, { capture: true, signal: controller.signal });
    inject(DestroyRef).onDestroy(() => controller.abort());
  }

  /** The image currently shown, or null when the lightbox is closed. */
  readonly active = computed<MediaLightboxState | null>(() => {
    const list = this.images();
    if (list.length === 0) return null;
    return list[this.cursor()] ?? null;
  });

  /** Total images in the open gallery (0 when closed). */
  readonly count = computed(() => this.images().length);
  /** 1-based position of the current image (0 when closed). */
  readonly position = computed(() =>
    this.images().length === 0 ? 0 : this.cursor() + 1
  );
  readonly hasPrev = computed(() => this.cursor() > 0);
  readonly hasNext = computed(() => this.cursor() < this.images().length - 1);

  /**
   * Source of the previous / next image, or null at the ends (or closed).
   * Exposed so the lightbox component can warm the browser cache for the
   * neighbours and avoid a flash/reflow when the user pages with arrows.
   */
  readonly prevSrc = computed(() => {
    const list = this.images();
    const i = this.cursor();
    return i > 0 ? list[i - 1]?.src ?? null : null;
  });
  readonly nextSrc = computed(() => {
    const list = this.images();
    const i = this.cursor();
    return i < list.length - 1 ? list[i + 1]?.src ?? null : null;
  });

  open(req: MediaLightboxRequest): void {
    const image = normalise(req);
    if (!image) return;
    this.images.set([image]);
    this.cursor.set(0);
  }

  openGallery(req: MediaLightboxGalleryRequest): void {
    const list: MediaLightboxImage[] = [];
    for (const candidate of req.images ?? []) {
      const image = normalise(candidate);
      if (image) list.push(image);
    }
    if (list.length === 0) return;
    const requested = req.index ?? 0;
    const index = clamp(requested, 0, list.length - 1);
    this.images.set(list);
    this.cursor.set(index);
  }

  next(): void {
    if (this.hasNext()) this.cursor.update((i) => i + 1);
  }

  prev(): void {
    if (this.hasPrev()) this.cursor.update((i) => i - 1);
  }

  close(): void {
    this.images.set([]);
    this.cursor.set(0);
  }
}

function normalise(req: MediaLightboxRequest | null | undefined): MediaLightboxImage | null {
  const src = (req?.src ?? '').trim();
  if (!src) return null;
  const actions = (req?.actions ?? [])
    .filter((action) => !!action.id && !!action.label)
    .map((action) => ({
      ...action,
      tooltip: (action.tooltip ?? '').toString(),
    }));
  return { src, alt: (req?.alt ?? '').toString(), actions };
}

function clamp(value: number, min: number, max: number): number {
  if (Number.isNaN(value)) return min;
  return Math.min(Math.max(Math.trunc(value), min), max);
}

import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  OnDestroy,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { RelatedTaskReference } from '../../../../../models/project-docs.model';
import {
  ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE,
  ISOLATED_HTML_ANCHORS_READY_MESSAGE,
  ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE,
  ISOLATED_HTML_TRACK_ANCHORS_MESSAGE,
} from '../../../../../services/sandboxed-html.util';
import {
  WikiLinkedElement,
  findWikiAnchor,
  scrollToWikiAnchor,
  wikiAnchorId,
  wikiLinkedElementKindLabel,
  wikiLinkedElementTitle,
} from '../wiki-linked-element';
import { WikiRelatedTasksComponent } from '../wiki-related-tasks/wiki-related-tasks.component';
import { MenuComponent, type MenuItemClickEvent } from '../../../../../components/menu';

type WikiAnchorState = 'pending' | 'available' | 'missing' | 'active';

@Component({
  selector: 'app-wiki-linked-elements',
  standalone: true,
  imports: [WikiRelatedTasksComponent, MenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-linked-elements.component.html',
  styleUrl: './wiki-linked-elements.component.scss',
})
export class WikiLinkedElementsComponent implements OnDestroy {
  readonly documentKey = input.required<string>();
  readonly frame = input<HTMLIFrameElement | null>(null);
  readonly links = input.required<readonly WikiLinkedElement[]>();
  readonly relatedTasks = input.required<RelatedTaskReference[]>();
  readonly hrefFor = input.required<(link: WikiLinkedElement) => string>();
  readonly navigate = output<{ link: WikiLinkedElement; reuse: 'replace-current' | 'new' }>();
  readonly contextLink = signal<WikiLinkedElement | null>(null);
  readonly contextPosition = signal({ x: 0, y: 0 });
  readonly contextItems = [{ kind: 'row' as const, id: 'open-new-tab', label: 'Open in new tab' }];

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly availability = signal<ReadonlyMap<string, 'pending' | 'available' | 'missing'>>(new Map());
  private readonly activeAnchorId = signal<string | null>(null);
  private boundScroller: HTMLElement | null = null;
  private boundFrame: HTMLIFrameElement | null = null;
  private documentObserver: MutationObserver | null = null;
  private scrollFrame: number | null = null;
  private commandedAnchorId: string | null = null;
  private restoreAnchorId: string | null = null;
  private boundDocumentKey: string | null = null;
  private bindVersion = 0;
  private destroyed = false;

  protected readonly linkKindLabel = wikiLinkedElementKindLabel;

  constructor() {
    effect(() => {
      const documentKey = this.documentKey();
      const frame = this.frame();
      const ids = this.anchorIds(this.links());
      if (documentKey !== this.boundDocumentKey) {
        this.boundDocumentKey = documentKey;
        this.commandedAnchorId = null;
        this.restoreAnchorId = null;
        this.activeAnchorId.set(null);
      } else {
        if (this.commandedAnchorId && !ids.includes(this.commandedAnchorId)) this.commandedAnchorId = null;
        if (this.restoreAnchorId && !ids.includes(this.restoreAnchorId)) this.restoreAnchorId = null;
        if (this.activeAnchorId() && !ids.includes(this.activeAnchorId()!)) this.activeAnchorId.set(null);
      }
      this.availability.set(new Map(ids.map(id => [id, 'pending'] as const)));
      const version = ++this.bindVersion;
      queueMicrotask(() => {
        if (this.destroyed || version !== this.bindVersion) return;
        this.ensureDocumentObserver();
        this.bindRenderedDocument(ids, this.isCurrentFrame(frame) ? frame : this.currentDocumentFrame());
      });
    });
  }

  @HostListener('window:message', ['$event'])
  onWindowMessage(event: MessageEvent): void {
    const frame = this.currentDocumentFrame();
    if (!frame || event.source !== frame.contentWindow) return;
    const message = event.data as { type?: unknown; anchors?: unknown; id?: unknown } | null;
    if (message?.type === ISOLATED_HTML_ANCHORS_READY_MESSAGE && Array.isArray(message.anchors)) {
      const available = new Set(message.anchors.filter((id): id is string => typeof id === 'string'));
      this.availability.set(new Map(this.anchorIds(this.links()).map(id => [
        id,
        available.has(id) ? 'available' : 'missing',
      ])));
      this.postTrackedAnchors(frame);
      if (this.commandedAnchorId && !available.has(this.commandedAnchorId)) this.commandedAnchorId = null;
      if (this.restoreAnchorId && !available.has(this.restoreAnchorId)) this.restoreAnchorId = null;
      this.restoreFramePosition(frame);
      return;
    }
    if (message?.type !== ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE) return;
    const id = typeof message.id === 'string' ? message.id : null;
    const activeId = id && this.anchorIds(this.links()).includes(id) ? id : null;
    this.activeAnchorId.set(activeId);
    if (activeId && (!this.commandedAnchorId || activeId === this.commandedAnchorId)) {
      this.restoreAnchorId = activeId;
      if (activeId === this.commandedAnchorId) this.commandedAnchorId = null;
    }
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.documentObserver?.disconnect();
    this.documentObserver = null;
    this.unbindRenderedDocument();
  }

  linkedElementState(link: WikiLinkedElement): WikiAnchorState | null {
    if (link.kind !== 'anchor') return null;
    const id = wikiAnchorId(link.target);
    if (!id) return 'missing';
    if (this.activeAnchorId() === id) return 'active';
    return this.availability().get(id) ?? 'pending';
  }

  linkedElementTitle(link: WikiLinkedElement): string {
    return this.linkedElementState(link) === 'missing'
      ? `Anchor not found in this document: ${link.target}`
      : wikiLinkedElementTitle(link);
  }

  openLinkedElement(event: MouseEvent, link: WikiLinkedElement): void {
    if (link.kind === 'external') return;
    event.preventDefault();
    if (link.kind !== 'anchor') {
      this.navigate.emit({ link, reuse: event.ctrlKey || event.metaKey ? 'new' : 'replace-current' });
      return;
    }

    const id = wikiAnchorId(link.target);
    if (!id || this.linkedElementState(link) === 'missing') return;
    const reader = this.wikiHost().querySelector<HTMLElement>('[data-testid="project-wiki-reader"]');
    if (reader && scrollToWikiAnchor(reader, link.target)) {
      this.commandedAnchorId = null;
      this.restoreAnchorId = id;
      this.activeAnchorId.set(id);
      return;
    }
    const frame = this.currentDocumentFrame();
    if (frame?.contentWindow) {
      this.commandedAnchorId = id;
      this.restoreAnchorId = id;
      this.postScrollAnchor(frame, id);
      return;
    }
    this.commandedAnchorId = null;
    this.restoreAnchorId = null;
    this.setAnchorAvailability(id, 'missing');
  }

  openLinkedElementInNewTab(event: MouseEvent, link: WikiLinkedElement): void {
    if (event.button !== 1 || link.kind === 'external' || link.kind === 'anchor') return;
    event.preventDefault();
    this.navigate.emit({ link, reuse: 'new' });
  }

  openContextMenu(event: MouseEvent, link: WikiLinkedElement): void {
    if (link.kind === 'external' || link.kind === 'anchor') return;
    event.preventDefault();
    this.contextLink.set(link);
    this.contextPosition.set({ x: event.clientX, y: event.clientY });
  }

  onContextItem(event: MenuItemClickEvent): void {
    const link = this.contextLink();
    if (event.id === 'open-new-tab' && link) this.navigate.emit({ link, reuse: 'new' });
    this.contextLink.set(null);
  }

  private bindRenderedDocument(ids: readonly string[], frame: HTMLIFrameElement | null): void {
    this.unbindRenderedDocument();
    if (frame) {
      this.boundFrame = frame;
      frame.addEventListener('load', this.onFrameLoad);
      this.postTrackedAnchors(frame);
      this.restoreFramePosition(frame);
      return;
    }

    const reader = this.wikiHost().querySelector<HTMLElement>('[data-testid="project-wiki-reader"]');
    if (!reader) return;
    this.availability.set(new Map(ids.map(id => [
      id,
      findWikiAnchor(reader, `#${encodeURIComponent(id)}`) ? 'available' : 'missing',
    ])));
    const scroller = reader.querySelector<HTMLElement>('.pwiki__reader-body');
    if (!scroller) return;
    this.boundScroller = scroller;
    scroller.addEventListener('scroll', this.onDocumentScroll, { passive: true });
    this.updateDirectActiveAnchor();
  }

  private readonly onFrameLoad = (): void => {
    if (!this.boundFrame) return;
    this.postTrackedAnchors(this.boundFrame);
    this.restoreFramePosition(this.boundFrame);
  };

  private readonly onDocumentScroll = (): void => {
    if (this.scrollFrame !== null) return;
    this.scrollFrame = requestAnimationFrame(() => {
      this.scrollFrame = null;
      this.updateDirectActiveAnchor();
    });
  };

  private updateDirectActiveAnchor(): void {
    const reader = this.wikiHost().querySelector<HTMLElement>('[data-testid="project-wiki-reader"]');
    const scroller = this.boundScroller;
    if (!reader || !scroller) return;
    const threshold = scroller.getBoundingClientRect().top + Math.min(96, scroller.clientHeight * 0.16);
    let active: string | null = null;
    for (const id of this.anchorIds(this.links())) {
      const element = findWikiAnchor(reader, `#${encodeURIComponent(id)}`);
      if (!element) continue;
      if (element.getBoundingClientRect().top <= threshold) active = id;
      else if (!active) { active = id; break; }
      else break;
    }
    this.activeAnchorId.set(active);
  }

  private postTrackedAnchors(frame: HTMLIFrameElement): void {
    frame.contentWindow?.postMessage({
      type: ISOLATED_HTML_TRACK_ANCHORS_MESSAGE,
      ids: this.anchorIds(this.links()),
    }, '*');
  }

  private postScrollAnchor(frame: HTMLIFrameElement, id: string): void {
    frame.contentWindow?.postMessage({ type: ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE, id }, '*');
  }

  private restoreFramePosition(frame: HTMLIFrameElement): void {
    const id = this.commandedAnchorId ?? this.restoreAnchorId;
    if (!id) return;
    this.commandedAnchorId = id;
    this.postScrollAnchor(frame, id);
  }

  private anchorIds(links: readonly WikiLinkedElement[]): string[] {
    return [...new Set(links
      .filter(link => link.kind === 'anchor')
      .map(link => wikiAnchorId(link.target))
      .filter((id): id is string => id !== null))];
  }

  private currentDocumentFrame(): HTMLIFrameElement | null {
    const frame = this.frame();
    if (this.isCurrentFrame(frame)) return frame;
    return this.wikiHost().querySelector<HTMLIFrameElement>('[data-testid="project-wiki-html-frame"]');
  }

  private isCurrentFrame(frame: HTMLIFrameElement | null): frame is HTMLIFrameElement {
    return !!frame && frame.isConnected && this.wikiHost().contains(frame);
  }

  private ensureDocumentObserver(): void {
    if (this.documentObserver) return;
    this.documentObserver = new MutationObserver(() => {
      const frame = this.currentDocumentFrame();
      if (frame === this.boundFrame) return;
      this.bindRenderedDocument(this.anchorIds(this.links()), frame);
    });
    this.documentObserver.observe(this.wikiHost(), { childList: true, subtree: true });
  }

  private wikiHost(): HTMLElement {
    return this.host.nativeElement.closest('app-project-wiki-section') as HTMLElement | null
      ?? this.host.nativeElement.ownerDocument.body;
  }

  private setAnchorAvailability(id: string, state: 'available' | 'missing'): void {
    const next = new Map(this.availability());
    next.set(id, state);
    this.availability.set(next);
  }

  private unbindRenderedDocument(): void {
    this.boundScroller?.removeEventListener('scroll', this.onDocumentScroll);
    this.boundScroller = null;
    this.boundFrame?.removeEventListener('load', this.onFrameLoad);
    this.boundFrame = null;
    if (this.scrollFrame !== null) cancelAnimationFrame(this.scrollFrame);
    this.scrollFrame = null;
  }
}

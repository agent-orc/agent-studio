import { ApplicationRef, ComponentRef } from '@angular/core';

const WIKI_FRAME_SELECTOR = '[data-testid="project-wiki-html-frame"]';

interface FrameStyleOptions {
  copyBodyClass?: boolean;
  copyStudioTheme?: boolean;
}

/** Shared DOM lifecycle for reference chips mounted into rendered documents. */
export abstract class RenderedReferenceHydrator {
  private observer: MutationObserver | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;

  start(): void {
    if (this.observer || typeof document === 'undefined') return;
    queueMicrotask(() => {
      this.observer = new MutationObserver(records => {
        this.cleanup(records);
        this.schedule();
      });
      this.observer.observe(document.body,
        { childList: true, subtree: true, characterData: true, attributes: true });
      this.schedule();
    });
  }

  protected refreshNow(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.scan();
  }

  protected collectRoots(selector: string): HTMLElement[] {
    const roots = Array.from(document.querySelectorAll<HTMLElement>(selector));
    document.querySelectorAll<HTMLIFrameElement>(WIKI_FRAME_SELECTOR).forEach(frame => {
      try {
        if (frame.contentDocument?.body) roots.push(frame.contentDocument.body);
      } catch {
        // Sandboxed cross-origin documents remain isolated and render unchanged.
      }
    });
    return roots;
  }

  protected textNodes(root: HTMLElement, acceptNode: (node: Text) => boolean): Text[] {
    const nodes: Text[] = [];
    const walker = root.ownerDocument.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: node => acceptNode(node as Text)
        ? NodeFilter.FILTER_ACCEPT
        : NodeFilter.FILTER_REJECT,
    });
    while (walker.nextNode()) nodes.push(walker.currentNode as Text);
    return nodes;
  }

  protected prepareFrameStyles(
    frameDocument: Document,
    marker: string,
    options: FrameStyleOptions = {},
  ): void {
    if (frameDocument.head.dataset[marker] === 'true') return;
    frameDocument.head.dataset[marker] = 'true';
    for (const style of Array.from(document.head.querySelectorAll('style, link[rel="stylesheet"]'))) {
      frameDocument.head.append(style.cloneNode(true));
    }
    frameDocument.documentElement.className = document.documentElement.className;
    if (options.copyBodyClass) frameDocument.body.className = document.body.className;
    if (options.copyStudioTheme) {
      const theme = document.documentElement.dataset['studioTheme'];
      if (theme) frameDocument.documentElement.dataset['studioTheme'] = theme;
    }
  }

  protected cleanupComponents<T>(
    records: readonly MutationRecord[],
    hostSelector: string,
    components: Map<HTMLElement, ComponentRef<T>>,
    app: ApplicationRef,
  ): void {
    for (const record of records) for (const removed of Array.from(record.removedNodes)) {
      if (!(removed instanceof HTMLElement)) continue;
      const hosts = removed.matches(hostSelector)
        ? [removed]
        : Array.from(removed.querySelectorAll<HTMLElement>(hostSelector));
      for (const host of hosts) {
        const ref = components.get(host);
        if (!ref) continue;
        app.detachView(ref.hostView);
        ref.destroy();
        components.delete(host);
      }
    }
  }

  private schedule(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = null;
      this.scan();
    }, 60);
  }

  protected abstract scan(): void;
  protected abstract cleanup(records: readonly MutationRecord[]): void;
}

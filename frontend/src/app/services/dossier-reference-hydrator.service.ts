import {
  ApplicationRef, ComponentRef, EnvironmentInjector, Injectable, createComponent, inject,
} from '@angular/core';
import { DossierReferenceChipComponent } from '../components/dossier-reference-chip/dossier-reference-chip';
import { DossierCatalogueService } from './dossier-catalogue.service';
import {
  dossierReferenceCandidates, resolveDossierReference,
  type DossierIndex, type DossierReference,
} from './dossier-reference.util';

const SCOPE_SELECTOR = '[data-dossier-reference-scope]';
const ROOT_SELECTOR = `cac-markdown, ${SCOPE_SELECTOR}`;
const HOST_TAG = 'app-dossier-reference-chip';
const SKIP_TEXT_PARENTS = `code, pre, kbd, samp, a, ${HOST_TAG}, app-task-reference-microcard`;

interface Occurrence { node: Text; start: number; end: number; reference: DossierReference }

/**
 * Turns every rendered mention of a Dossier into the Dossier chip (AGT-2812).
 *
 * The markdown surfaces (result view, review documents, task prompt, activity,
 * Wiki page bodies) all render through the shared `cac-markdown` element or the
 * Wiki HTML frame, so one host-owned hydrator reaches all of them without each
 * surface reimplementing recognition. Any other surface can opt in by marking a
 * subtree with `data-dossier-reference-scope="<project name>"`, which doubles as
 * the project hint for repo-relative paths.
 *
 * Mentions arrive as prose, as inline code, and as plain file links; all three
 * are hydrated. Fenced code blocks stay untouched: a `<pre>` sample is source,
 * not a reference.
 */
@Injectable({ providedIn: 'root' })
export class DossierReferenceHydratorService {
  private readonly catalogue = inject(DossierCatalogueService);
  private readonly app = inject(ApplicationRef);
  private readonly injector = inject(EnvironmentInjector);
  private readonly components = new Map<HTMLElement, ComponentRef<DossierReferenceChipComponent>>();
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

  /** Run one immediate pass. Used after a catalogue reload and by tests. */
  refresh(): void {
    if (this.timer) clearTimeout(this.timer);
    this.scan();
  }

  private schedule(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.scan(), 60);
  }

  private scan(): void {
    this.timer = null;
    const roots = this.collectRoots();
    if (!roots.length) return;
    this.catalogue.ensureLoaded().subscribe(index => {
      if (!index.size) return;
      for (const root of roots) this.hydrateRoot(root, index);
    });
  }

  private collectRoots(): HTMLElement[] {
    const roots = Array.from(document.querySelectorAll<HTMLElement>(ROOT_SELECTOR));
    document.querySelectorAll<HTMLIFrameElement>('[data-testid="project-wiki-html-frame"]')
      .forEach(frame => {
        try {
          if (frame.contentDocument?.body) roots.push(frame.contentDocument.body);
        } catch {
          // Sandboxed cross-origin documents remain isolated and render unchanged.
        }
      });
    return roots;
  }

  private hydrateRoot(root: HTMLElement, index: DossierIndex): void {
    const hint = projectHint(root);
    this.hydrateElements(root, index, hint);
    this.hydrateText(root, index, hint);
  }

  /** Plain file links and inline code spans naming one Dossier. */
  private hydrateElements(root: HTMLElement, index: DossierIndex, hint: string | null): void {
    for (const element of Array.from(root.querySelectorAll<HTMLElement>('a[href], code'))) {
      if (!element.isConnected || element.closest(HOST_TAG)) continue;
      if (element.tagName === 'CODE' && (element.closest('pre') || element.closest('a'))) continue;
      const tokens = element.tagName === 'A'
        ? [element.getAttribute('href') ?? '', element.textContent ?? '']
        : [element.textContent ?? ''];
      const reference = tokens
        .map(token => resolveDossierReference(hrefToken(token), index, hint))
        .find(Boolean);
      if (!reference) continue;
      element.replaceWith(this.createHost(reference, element.ownerDocument));
    }
  }

  private hydrateText(root: HTMLElement, index: DossierIndex, hint: string | null): void {
    const occurrences: Occurrence[] = [];
    const walker = (root.ownerDocument ?? document).createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: node => node.parentElement?.closest(SKIP_TEXT_PARENTS)
        ? NodeFilter.FILTER_REJECT
        : NodeFilter.FILTER_ACCEPT,
    });
    while (walker.nextNode()) {
      const node = walker.currentNode as Text;
      for (const candidate of dossierReferenceCandidates(node.textContent || '')) {
        const reference = resolveDossierReference(candidate.token, index, hint);
        if (reference) occurrences.push({ node, ...candidate, reference });
      }
    }
    this.replaceText(occurrences);
  }

  private replaceText(occurrences: readonly Occurrence[]): void {
    const grouped = new Map<Text, Occurrence[]>();
    for (const occurrence of occurrences) {
      if (!occurrence.node.isConnected) continue;
      grouped.set(occurrence.node, [...(grouped.get(occurrence.node) ?? []), occurrence]);
    }
    for (const [node, list] of grouped) {
      const source = node.textContent || '';
      const fragment = document.createDocumentFragment();
      let cursor = 0;
      for (const occurrence of list.sort((a, b) => a.start - b.start)) {
        if (occurrence.start < cursor) continue;
        fragment.append(source.slice(cursor, occurrence.start));
        fragment.append(this.createHost(occurrence.reference, node.ownerDocument));
        cursor = occurrence.end;
      }
      fragment.append(source.slice(cursor));
      node.replaceWith(fragment);
    }
  }

  private createHost(reference: DossierReference, ownerDocument: Document): HTMLElement {
    if (ownerDocument !== document) prepareFrameStyles(ownerDocument);
    const host = ownerDocument.createElement(HOST_TAG);
    host.dataset['dossierId'] = reference.id;
    const ref = createComponent(DossierReferenceChipComponent,
      { hostElement: host, environmentInjector: this.injector });
    ref.setInput('reference', reference);
    this.app.attachView(ref.hostView);
    ref.changeDetectorRef.detectChanges();
    this.components.set(host, ref);
    return host;
  }

  private cleanup(records: readonly MutationRecord[]): void {
    for (const record of records) for (const removed of Array.from(record.removedNodes)) {
      if (!(removed instanceof HTMLElement)) continue;
      const hosts = removed.matches(HOST_TAG)
        ? [removed]
        : Array.from(removed.querySelectorAll<HTMLElement>(HOST_TAG));
      for (const host of hosts) {
        const ref = this.components.get(host);
        if (!ref) continue;
        this.app.detachView(ref.hostView);
        ref.destroy();
        this.components.delete(host);
      }
    }
  }
}

/** The nearest opted-in scope names the project a repo-relative path belongs to. */
function projectHint(root: HTMLElement): string | null {
  const scope = root.closest?.(SCOPE_SELECTOR) as HTMLElement | null;
  return scope?.dataset['dossierReferenceScope']?.trim() || null;
}

/** An `<a href>` may carry a hash route, a query, or a plain repo-relative path. */
function hrefToken(value: string): string {
  return value.trim().split(/[?#]/)[0];
}

function prepareFrameStyles(frameDocument: Document): void {
  if (frameDocument.head.dataset['dossierReferenceStyles'] === 'true') return;
  frameDocument.head.dataset['dossierReferenceStyles'] = 'true';
  for (const style of Array.from(
    document.head.querySelectorAll('style, link[rel="stylesheet"]'))) {
    frameDocument.head.append(style.cloneNode(true));
  }
  // The chip reads semantic tokens, and the token set is selected by the host
  // theme marker; without it the frame would render one theme only.
  frameDocument.documentElement.className = document.documentElement.className;
  const theme = document.documentElement.dataset['studioTheme'];
  if (theme) frameDocument.documentElement.dataset['studioTheme'] = theme;
}

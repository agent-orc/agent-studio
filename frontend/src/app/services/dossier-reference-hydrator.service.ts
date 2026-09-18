import {
  ApplicationRef, ComponentRef, EnvironmentInjector, Injectable, createComponent, inject,
} from '@angular/core';
import { DossierReferenceChipComponent } from '../components/dossier-reference-chip/dossier-reference-chip';
import { DossierCatalogueService } from './dossier-catalogue.service';
import {
  dossierReferenceCandidates, resolveDossierReference,
  type DossierIndex, type DossierReference,
} from './dossier-reference.util';
import { RenderedReferenceHydrator } from './rendered-reference-hydrator';

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
export class DossierReferenceHydratorService extends RenderedReferenceHydrator {
  private readonly catalogue = inject(DossierCatalogueService);
  private readonly app = inject(ApplicationRef);
  private readonly injector = inject(EnvironmentInjector);
  private readonly components = new Map<HTMLElement, ComponentRef<DossierReferenceChipComponent>>();
  /** Run one immediate pass. Used by tests and explicit surface refreshes. */
  refresh(): void {
    this.refreshNow();
  }

  protected override scan(): void {
    const roots = this.collectRoots(ROOT_SELECTOR);
    if (!roots.length) return;
    this.catalogue.ensureLoaded().subscribe(index => {
      if (!index.size) return;
      for (const root of roots) this.hydrateRoot(root, index);
    });
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
    for (const node of this.textNodes(root,
      node => !node.parentElement?.closest(SKIP_TEXT_PARENTS))) {
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
    if (ownerDocument !== document) {
      this.prepareFrameStyles(ownerDocument, 'dossierReferenceStyles', { copyStudioTheme: true });
    }
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

  protected override cleanup(records: readonly MutationRecord[]): void {
    this.cleanupComponents(records, HOST_TAG, this.components, this.app);
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

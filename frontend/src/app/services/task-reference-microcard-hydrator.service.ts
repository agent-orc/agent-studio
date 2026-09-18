import { ApplicationRef, ComponentRef, EnvironmentInjector, Injectable, createComponent, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { TaskReferenceMicrocardComponent, TaskReferenceStatus } from '../components/task-reference-microcard/task-reference-microcard';
import { RenderedReferenceHydrator } from './rendered-reference-hydrator';

interface StatusResponse { items: TaskReferenceStatus[]; }
interface Occurrence { node: Text; start: number; end: number; key: string; }

const KEY_PATTERN = /(^|[^A-Za-z0-9_-])([A-Z][A-Z0-9]{1,5}-\d+)(?=$|[^A-Za-z0-9_-])/gi;

@Injectable({ providedIn: 'root' })
export class TaskReferenceMicrocardHydratorService extends RenderedReferenceHydrator {
  private readonly http = inject(HttpClient);
  private readonly app = inject(ApplicationRef);
  private readonly injector = inject(EnvironmentInjector);
  private readonly cache = new Map<string, TaskReferenceStatus | null>();
  private readonly components = new Map<HTMLElement, ComponentRef<TaskReferenceMicrocardComponent>>();
  protected override scan(): void {
    const occurrences = this.collectOccurrences();
    if (!occurrences.length) return;
    const keys = [...new Set(occurrences.map(o => o.key))];
    const missing = keys.filter(k => !this.cache.has(k));
    if (!missing.length) return this.render(occurrences);
    this.http.post<StatusResponse>('/api/tasks/reference-status', { keys: missing }).subscribe({
      next: response => {
        for (const key of missing) this.cache.set(key, null);
        for (const item of response.items) this.cache.set(item.key.toUpperCase(), item);
        this.render(occurrences);
      },
      // A denied or failed batch (e.g. the public-demo edge's typed
      // unsafe-method denial) still marks the keys resolved so the next
      // mutation-triggered scan does not retry them forever; the microcards
      // for this batch simply stay unrendered.
      error: () => {
        for (const key of missing) this.cache.set(key, null);
      },
    });
  }

  private collectOccurrences(): Occurrence[] {
    const result: Occurrence[] = [];
    const roots = this.collectRoots('cac-markdown');
    roots.forEach(root => {
      for (const node of this.textNodes(root, node => {
        const parent = node.parentElement;
        return !!parent
          && !parent.closest('code, pre, kbd, samp, app-task-reference-microcard')
          && taskReferenceCandidates(node.textContent || '').length > 0;
      })) {
        const text = node.textContent || '';
        for (const candidate of taskReferenceCandidates(text)) {
          result.push({ node, ...candidate });
        }
      }
    });
    return result;
  }

  private render(occurrences: Occurrence[]): void {
    const grouped = new Map<Text, Occurrence[]>();
    for (const occurrence of occurrences) {
      if (!this.cache.get(occurrence.key) || !occurrence.node.isConnected) continue;
      const list = grouped.get(occurrence.node) || [];
      list.push(occurrence);
      grouped.set(occurrence.node, list);
    }
    for (const [node, list] of grouped) {
      const existingAnchor = node.parentElement?.closest<HTMLAnchorElement>('a[data-task-ref="true"]');
      if (existingAnchor && list.length === 1) {
        const status = this.cache.get(list[0].key)!;
        const host = this.createHost(status, list[0].key, node.ownerDocument);
        existingAnchor.replaceWith(host);
        continue;
      }
      const source = node.textContent || '';
      const fragment = document.createDocumentFragment();
      let cursor = 0;
      for (const occurrence of list.sort((a, b) => a.start - b.start)) {
        fragment.append(source.slice(cursor, occurrence.start));
        const status = this.cache.get(occurrence.key)!;
        const host = this.createHost(status, occurrence.key, node.ownerDocument);
        fragment.append(host);
        cursor = occurrence.end;
      }
      fragment.append(source.slice(cursor));
      node.replaceWith(fragment);
    }
  }

  private createHost(status: TaskReferenceStatus, key: string, ownerDocument: Document): HTMLElement {
    if (ownerDocument !== document) {
      this.prepareFrameStyles(ownerDocument, 'taskReferenceStyles', { copyBodyClass: true });
    }
    const host = ownerDocument.createElement('app-task-reference-microcard');
    host.dataset['taskReferenceKey'] = key;
    const ref = createComponent(TaskReferenceMicrocardComponent, { hostElement: host, environmentInjector: this.injector });
    ref.setInput('status', status);
    this.app.attachView(ref.hostView);
    ref.changeDetectorRef.detectChanges();
    this.components.set(host, ref);
    return host;
  }

  protected override cleanup(records: readonly MutationRecord[]): void {
    this.cleanupComponents(records, 'app-task-reference-microcard', this.components, this.app);
  }
}

export function taskReferenceCandidates(text: string): { start: number; end: number; key: string }[] {
  const result: { start: number; end: number; key: string }[] = [];
  KEY_PATTERN.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = KEY_PATTERN.exec(text))) {
    const start = match.index + (match[1]?.length || 0);
    result.push({ start, end: start + match[2].length, key: match[2].toUpperCase() });
  }
  return result;
}

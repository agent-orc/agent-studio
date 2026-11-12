import { HttpClient, HttpParams } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { TASK_REFERENCE_CHAT_PROVIDER } from '../../../services/task-reference-chat.provider';
import { TaskReferenceNavigationService } from '../../../services/task-reference-navigation.service';
import { DossierCatalogueService } from '../../../services/dossier-catalogue.service';
import { DossierReferenceNavigationService } from '../../../services/dossier-reference-navigation.service';
import { resolveDossierReference } from '../../../services/dossier-reference.util';
import { StudioTabStateService } from '../../studio-shell/services/studio-tab-state.service';
import { BatchPanelComponent } from '../../batch-gate/batch-panel/batch-panel';

export interface MagazineAuthor { principal: string; persona: string | null; personaId?: string | null; roleId?: string | null }
export interface MagazineSource { kind: string; ref: string }
export interface MagazineSummary { scope: string; slug: string; revision: number; title: string; summary: string; kind: string; authors: MagazineAuthor[]; sources: MagazineSource[]; updatedAt: string; pinned: boolean }
export interface MagazineArticle extends MagazineSummary { body: string; currentRevision: number; author: MagazineAuthor }
export interface MagazineHistory { revisions: { revision: number; at: string; author: MagazineAuthor; title: string }[] }
export interface MagazineChild { scope: string; label: string }

@Component({
  selector: 'app-magazine-view', standalone: true,
  imports: [MarkdownViewComponent, BatchPanelComponent], providers: [TASK_REFERENCE_CHAT_PROVIDER],
  templateUrl: './magazine-view.html', styleUrl: './magazine-view.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MagazineViewComponent implements OnDestroy {
  readonly scope = input.required<string>();
  readonly label = input<string>('Magazine');
  readonly children = input<readonly MagazineChild[]>([]);
  readonly projects = input<readonly MagazineChild[]>([]);
  private readonly http = inject(HttpClient);
  private readonly tabs = inject(StudioTabStateService);
  private readonly tasks = inject(TaskReferenceNavigationService);
  private readonly dossier = inject(DossierCatalogueService);
  private readonly dossierNav = inject(DossierReferenceNavigationService);
  private readonly bodyHost = viewChild<ElementRef<HTMLElement>>('bodyHost');
  private readonly batchPanel = viewChild<BatchPanelComponent>('batchPanel');
  readonly batchTarget = signal<string | null>(null);
  readonly batchProject = computed(() => this.projects().find(project => project.scope === this.scope()) ?? null);
  readonly closeBatch = () => this.batchTarget.set(null);
  readonly articles = signal<MagazineSummary[]>([]);
  readonly article = signal<MagazineArticle | null>(null);
  readonly history = signal<MagazineHistory | null>(null);
  readonly busy = signal(true);
  readonly error = signal(false);
  private revisionSeen = signal<Record<string, number>>({});
  private observer: MutationObserver | null = null;
  readonly sortedArticles = computed(() => [...this.articles()].sort((a, b) => Number(b.pinned) - Number(a.pinned) || Date.parse(b.updatedAt) - Date.parse(a.updatedAt)));

  constructor() {
    effect(onCleanup => {
      const scope = this.scope();
      this.article.set(null); this.history.set(null); this.busy.set(true); this.error.set(false);
      try { this.revisionSeen.set(JSON.parse(localStorage.getItem('magazine-seen') || '{}')); } catch { this.revisionSeen.set({}); }
      const subscription = this.http.get<{ articles: MagazineSummary[] }>('/api/magazine', { params: new HttpParams().set('scope', scope) }).subscribe({
        next: result => { this.articles.set(result.articles ?? []); this.busy.set(false); },
        error: () => { this.error.set(true); this.busy.set(false); },
      });
      onCleanup(() => subscription.unsubscribe());
    });
    effect(() => {
      this.observer?.disconnect();
      this.observer = null;
      const article = this.article();
      const host = this.bodyHost()?.nativeElement;
      if (!article || !host) return;
      this.dossier.ensureLoaded().subscribe(() => this.scheduleLinkify());
      this.observer = new MutationObserver(() => this.scheduleLinkify());
      this.observer.observe(host, { childList: true, subtree: true });
      this.scheduleLinkify();
    });
    effect(() => {
      const id = this.batchTarget();
      const panel = this.batchPanel();
      if (id && panel) panel.openBatch(id);
    });
  }

  ngOnDestroy(): void { this.observer?.disconnect(); }
  byline(author: MagazineAuthor): string {
    const role = author.roleId?.replace(/[-_]/g, ' ').replace(/\b\w/g, letter => letter.toUpperCase());
    return [author.persona || author.principal, role].filter(Boolean).join(' · ');
  }
  authors(article: MagazineSummary): string { return article.authors.map(author => this.byline(author)).join(', '); }
  relativeTime(at: string): string {
    const minutes = Math.max(0, Math.floor((Date.now() - Date.parse(at)) / 60000));
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'} ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ago`;
    const days = Math.floor(hours / 24);
    return `${days} day${days === 1 ? '' : 's'} ago`;
  }
  marker(article: MagazineSummary): string | null {
    const seen = this.revisionSeen()[`${article.scope}/${article.slug}`];
    return seen == null ? 'New' : seen < article.revision ? 'Updated' : null;
  }
  open(summary: MagazineSummary, revision?: number): void {
    const scope = encodeURIComponent(summary.scope), slug = encodeURIComponent(summary.slug);
    const params = revision ? { params: new HttpParams().set('revision', revision) } : {};
    this.http.get<MagazineArticle>(`/api/magazine/${scope}/${slug}`, params).subscribe({ next: article => {
      this.article.set(article);
      if (!revision) {
        const seen = { ...this.revisionSeen(), [`${summary.scope}/${summary.slug}`]: article.currentRevision };
        this.revisionSeen.set(seen);
        try { localStorage.setItem('magazine-seen', JSON.stringify(seen)); } catch { /* private browsing */ }
      }
    }, error: () => this.error.set(true) });
    this.http.get<MagazineHistory>(`/api/magazine/${scope}/${slug}/history`).subscribe({ next: history => this.history.set(history), error: () => this.history.set(null) });
  }
  back(): void { this.article.set(null); this.history.set(null); }
  openScope(child: MagazineChild): void {
    if (child.scope.startsWith('project:')) {
      this.tabs.open({ kind: 'hub', projectName: child.label, section: 'magazine' });
    } else this.tabs.open({ kind: 'magazine', scope: child.scope, label: child.label });
  }
  sourceLink(source: MagazineSource): boolean {
    if (source.kind === 'card') return this.tasks.openReference(source.ref);
    if (source.kind === 'dossier') {
      const reference = resolveDossierReference(source.ref, this.dossier.index(), this.batchProject()?.label ?? null);
      if (reference) { this.dossierNav.openDossier(reference); return true; }
    }
    if (source.kind === 'batch') {
      if (this.projects().some(project => project.scope === this.scope())) {
        this.batchTarget.set(source.ref);
        return true;
      }
    }
    return false;
  }
  sourceResolved(source: MagazineSource): boolean {
    if (source.kind === 'card') return this.tasks.markdownReferences().some(ref => ref.label.toUpperCase() === source.ref.toUpperCase());
    if (source.kind === 'dossier') return !!resolveDossierReference(source.ref, this.dossier.index(), this.batchProject()?.label ?? null);
    return source.kind === 'batch' && this.projects().some(project => project.scope === this.scope());
  }
  private scheduled = false;
  private scheduleLinkify(): void {
    if (this.scheduled) return;
    this.scheduled = true;
    queueMicrotask(() => { this.scheduled = false; this.linkify(); });
  }
  private linkify(): void {
    const root = this.bodyHost()?.nativeElement;
    if (!root) return;
    const projects = this.projects();
    const names = projects.map(p => p.label).filter(Boolean).sort((a, b) => b.length - a.length);
    const pattern = /(?:\b[A-Z]{2,6}-\d+\b|docs\/[\w./-]+\/|\bbatch-[\w-]+\b)/g;
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    const nodes: Text[] = [];
    while (walker.nextNode()) {
      const node = walker.currentNode as Text;
      if (node.parentElement?.closest('a, code, pre, button, script, style, app-task-reference-microcard, app-dossier-reference-chip')) continue;
      nodes.push(node);
    }
    for (const node of nodes) {
      const source = node.textContent || '';
      const matches: { start: number; end: number; action: () => void }[] = [];
      for (const match of source.matchAll(pattern)) {
        const token = match[0];
        const dossier = resolveDossierReference(token, this.dossier.index(), this.batchProject()?.label ?? null);
        if (dossier) matches.push({ start: match.index, end: match.index + token.length, action: () => this.dossierNav.openDossier(dossier) });
        else if (this.tasks.markdownReferences().some(ref => ref.label.toUpperCase() === token.toUpperCase())) matches.push({ start: match.index, end: match.index + token.length, action: () => this.tasks.openReference(token) });
        else if (token.startsWith('batch-')) {
          const project = projects.find(p => p.scope === this.scope());
          if (project) matches.push({ start: match.index, end: match.index + token.length, action: () => this.batchTarget.set(token) });
        }
      }
      for (const name of names) {
        let at = source.indexOf(name);
        while (at >= 0) {
          const project = projects.find(p => p.label === name)!;
          matches.push({ start: at, end: at + name.length, action: () => this.tabs.open({ kind: 'hub', projectName: project.label }) });
          at = source.indexOf(name, at + name.length);
        }
      }
      if (!matches.length) continue;
      const fragment = document.createDocumentFragment(); let end = 0;
      for (const match of matches.sort((a, b) => a.start - b.start || b.end - a.end)) {
        if (match.start < end) continue;
        fragment.append(source.slice(end, match.start));
        const link = document.createElement('a'); link.href = '#'; link.textContent = source.slice(match.start, match.end);
        link.addEventListener('click', event => { event.preventDefault(); match.action(); });
        fragment.append(link); end = match.end;
      }
      fragment.append(source.slice(end)); node.replaceWith(fragment);
    }
  }
}

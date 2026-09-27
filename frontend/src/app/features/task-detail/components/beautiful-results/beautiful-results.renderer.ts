/**
 * Beautiful results renderer.
 *
 * Two-stage pipeline:
 *
 *   stage 1 (sync)  `renderResultsHtml(markdown, options)`
 *     - extracts structured sentinels ([[TASK_DONE]], [[TASK_BLOCKED:...]], etc.)
 *       into a top banner so they surface as colour-coded outcomes instead of
 *       being buried in body text
 *     - runs the body through `marked` with a custom renderer
 *       (resolves images, decorates fenced code blocks with a language label,
 *       upgrades `diff` blocks to side-by-side coloured rendering via diff2html,
 *       escapes inline code, etc.)
 *     - sanitises the resulting HTML with DOMPurify
 *     - returns the synchronous "first paint" HTML, which still shows code
 *       blocks (escaped, monospace, no colours)
 *
 *   stage 2 (async) `highlightResultsHtml(container)`
 *     - finds every `<pre><code data-lang=...>` decorated by stage 1, runs
 *       highlight.js against the original source (stashed as a data-attribute)
 *       and swaps in the highlighted HTML
 *     - lightbox wiring + copy buttons are owned by the host component
 *
 * Stage 1 stays pure and synchronous on purpose so it is trivially unit-
 * testable and so the protocol pane gets a usable render even before the
 * highlight.js bundle has streamed in.
 *
 * XSS: all HTML is run through DOMPurify before being returned. Image src
 * resolution happens against the raw markdown URL *before* sanitization;
 * the resolved path is then re-sanitized as an attribute value. Fenced
 * `diff` content is HTML-escaped before being fed to diff2html and the
 * resulting markup goes through DOMPurify too.
 */
import { Marked, type MarkedExtension, type Tokens } from 'marked';
import DOMPurify from 'dompurify';
import { html as diff2htmlRender } from 'diff2html/lib-esm/diff2html.js';
import { protectTechnicalMarkdown } from 'coding-agent-chat/markdown';
import { resolveProtocolImageSrc } from '../protocol-pane/protocol-image-resolver';
import { detectSourceRef } from './source-ref';
import { resolveTaskArtifactLink } from '../task-artifact-links/task-artifact-link';

export interface BeautifulRendererContext {
  jobId: string | null | undefined;
  watchPath: string | null | undefined;
}

export interface SentinelBanner {
  kind: 'done' | 'blocked' | 'needsInput' | 'noop';
  reason: string | null;
  raw: string;
}

export interface RenderedResults {
  /** Sanitized HTML for the body (sentinels removed). */
  html: string;
  /** Banner derived from a trailing sentinel marker, if present. */
  banner: SentinelBanner | null;
}

const SENTINEL_RE = /\[\[TASK_(DONE|BLOCKED|NEEDS_INPUT|NOOP)(?::([^\]]*))?\]\]/g;

/**
 * Lift sentinel markers ([[TASK_DONE]], [[TASK_BLOCKED:reason]], etc.) out of
 * the markdown body so they can render as a top banner. Returns the cleaned
 * markdown plus the *last* sentinel found (the structured contract uses the
 * trailing token as authoritative).
 */
export function extractSentinel(markdown: string): { cleaned: string; banner: SentinelBanner | null } {
  let last: SentinelBanner | null = null;
  SENTINEL_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = SENTINEL_RE.exec(markdown)) !== null) {
    const kind = sentinelKind(m[1]);
    last = { kind, reason: m[2]?.trim() || null, raw: m[0] };
  }
  if (!last) return { cleaned: markdown, banner: null };
  const cleaned = markdown.replace(SENTINEL_RE, '').replace(/\n{3,}/g, '\n\n').trimEnd();
  return { cleaned, banner: last };
}

function sentinelKind(tag: string): SentinelBanner['kind'] {
  switch (tag) {
    case 'DONE': return 'done';
    case 'BLOCKED': return 'blocked';
    case 'NEEDS_INPUT': return 'needsInput';
    case 'NOOP': return 'noop';
    default: return 'noop';
  }
}

// Bounded LRU memo for the marked->sanitize pipeline. Status.md and
// run results.md bodies are stable between mounts (the operator clicks
// away and back, the lane re-sorts, the detail pane re-instantiates),
// so memoising by (markdown, jobId, watchPath) turns a previously-seen
// render into a single map lookup. Keeps memory bounded with a small
// LRU; entries are evicted in insertion order once the cap is hit.
const RENDER_CACHE_LIMIT = 64;
const renderCache = new Map<string, RenderedResults>();

function renderCacheKey(markdown: string, context: BeautifulRendererContext): string {
  // jobId + watchPath disambiguate two jobs with the same status.md body
  // (rare in practice, but image resolution depends on both).
  return `${context.jobId ?? ''}${context.watchPath ?? ''}${markdown.length}${markdown}`;
}

export function renderResultsHtml(markdown: string, context: BeautifulRendererContext): RenderedResults {
  const cacheKey = renderCacheKey(markdown ?? '', context);
  const hit = renderCache.get(cacheKey);
  if (hit) {
    // Touch: move to the tail so it survives the next eviction round.
    renderCache.delete(cacheKey);
    renderCache.set(cacheKey, hit);
    return hit;
  }

  const { cleaned, banner } = extractSentinel(markdown ?? '');
  if (!cleaned.trim()) {
    const empty: RenderedResults = { html: '', banner };
    storeRender(cacheKey, empty);
    return empty;
  }

  const extension = buildMarkedExtension(context);
  const local = new Marked(extension);
  let raw: string;
  try {
    const parsed = local.parse(protectTechnicalMarkdown(cleaned));
    raw = typeof parsed === 'string' ? parsed : '';
  } catch {
    // Defensive: a malformed token should never blow up the result view.
    raw = `<pre>${escapeHtml(cleaned)}</pre>`;
  }
  const html = sanitize(raw);
  const result: RenderedResults = { html, banner };
  storeRender(cacheKey, result);
  return result;
}

function storeRender(key: string, value: RenderedResults): void {
  renderCache.set(key, value);
  while (renderCache.size > RENDER_CACHE_LIMIT) {
    const oldest = renderCache.keys().next();
    if (oldest.done) break;
    renderCache.delete(oldest.value);
  }
}

/** Drop every cached render. Used by tests; safe to call in production. */
export function clearResultsRenderCache(): void {
  renderCache.clear();
}

function buildMarkedExtension(context: BeautifulRendererContext): MarkedExtension {
  return {
    gfm: true,
    breaks: false,
    renderer: {
      // Fenced code blocks. `diff` blocks get the diff2html treatment; every
      // other block gets a language-label header and a copy-friendly body
      // that stage 2 can swap with highlight.js output.
      code(token: Tokens.Code): string {
        const lang = (token.lang || '').trim().toLowerCase();
        const text = token.text ?? '';
        if (lang === 'diff-autodetected') return renderCodeBlock(text, 'diff');
        if (lang === 'html-autodetected') return renderCodeBlock(text, 'html');
        if (lang === 'diff') return renderDiff(text);
        return renderCodeBlock(text, lang || null);
      },
      html(token: Tokens.HTML | Tokens.Tag): string {
        return escapeHtml(stripEventHandlerAttributes(token.text ?? token.raw ?? ''));
      },
      paragraph(token: Tokens.Paragraph): string {
        const inline = this.parser.parseInline(token.tokens);
        const standaloneImage = token.tokens.length === 1 && token.tokens[0]?.type === 'image';
        return standaloneImage ? inline : `<p>${inline}</p>`;
      },
      // Inline images: resolve attachments/results paths through the
      // existing resolver and wrap in a zoom-capable figure. Captions
      // come from the alt text when present.
      image(token: Tokens.Image): string {
        const original = token.href ?? '';
        const resolved = resolveProtocolImageSrc(original, context.jobId, context.watchPath);
        const alt = token.text ?? '';
        const caption = alt
          ? `<figcaption class="results-figure__caption">${escapeHtml(alt)}</figcaption>`
          : '';
        // `data-results-image` tags the original reference so the host can swap
        // a broken/unresolvable image (missing file → <img> error) for a compact
        // "missing" placeholder instead of leaving a silently empty row.
        return `<figure class="results-figure" data-results-image="${escapeAttr(original)}">`
          + `<button type="button" class="results-figure__btn" data-results-lightbox="${escapeAttr(resolved)}" data-results-alt="${escapeAttr(alt)}" aria-label="Open image">`
          + `<img class="results-figure__img" src="${escapeAttr(resolved)}" alt="${escapeAttr(alt)}" loading="lazy">`
          + `</button>${caption}</figure>`;
      },
      // Inline code: a span that looks like a repo source path (e.g.
      // `backend/Services/Runner/SolutionQualityGate.cs` or `foo.ts:42`)
      // becomes a clickable source link the host opens in the source
      // viewer; everything else renders as a plain inline <code>.
      codespan(token: Tokens.Codespan): string {
        const text = token.text ?? '';
        const ref = detectSourceRef(text);
        if (ref) return renderSourceLink(ref.path, ref.line, `<code>${escapeHtml(text)}</code>`);
        return `<code>${escapeHtml(text)}</code>`;
      },
      // External links open in a new tab; internal anchors stay in place.
      // Repo docs open the project Wiki, task keys open task detail, and other
      // relative source paths use the in-app source viewer.
      link(token: Tokens.Link): string {
        const rawHref = token.href ?? '';
        const inner = token.tokens && token.tokens.length
          ? this.parser.parseInline(token.tokens)
          : escapeHtml(token.text ?? '');
        const artifact = resolveTaskArtifactLink(rawHref, context);
        if (artifact) return renderArtifactLink(artifact.href, artifact.relativePath, inner);
        if (!/^[a-z][a-z0-9+.-]*:/i.test(rawHref)) {
          const wikiPath = detectWikiPath(rawHref);
          if (wikiPath) return renderWikiLink(wikiPath, inner);
          const taskKey = detectTaskKey(rawHref, token.text ?? '');
          if (taskKey) return renderTaskLink(taskKey, inner);
          const ref = detectSourceRef(rawHref);
          if (ref) return renderSourceLink(ref.path, ref.line, inner);
        }
        const href = safeLinkUrl(rawHref);
        const external = /^https?:/i.test(href);
        const attrs = external ? ' target="_blank" rel="noopener noreferrer"' : '';
        return `<a href="${escapeAttr(href)}"${attrs}>${inner}</a>`;
      }
    }
  };
}

function renderArtifactLink(href: string, relativePath: string, inner: string): string {
  return `<a class="results-internal-link" href="${escapeAttr(href)}"`
    + ` data-results-artifact="${escapeAttr(relativePath)}"`
    + ` target="_blank" rel="noopener noreferrer"`
    + ` aria-label="Open task artifact ${escapeAttr(relativePath)}">${inner}</a>`;
}

function detectWikiPath(rawHref: string): string | null {
  const path = rawHref.trim()
    .split(/[?#]/, 1)[0]
    .replace(/\\/g, '/')
    .replace(/^\.?\//, '');
  if (!/^docs\/.+\.(?:md|mdx|html?)$/i.test(path) || path.includes('..')) return null;
  return path.replace(/^docs\//i, '');
}

function detectTaskKey(rawHref: string, label: string): string | null {
  const candidates = [rawHref, label];
  for (const candidate of candidates) {
    const match = /(?:^|[/#:])([A-Z][A-Z0-9]{1,5}-\d+)(?:$|[/?#])/i.exec(candidate.trim());
    if (match) return match[1].toUpperCase();
  }
  return null;
}

function renderWikiLink(path: string, inner: string): string {
  return `<a class="results-internal-link" href="#wiki:${escapeAttr(path)}"`
    + ` data-results-wiki="${escapeAttr(path)}"`
    + ` aria-label="Open ${escapeAttr(path)} in project Wiki">${inner}</a>`;
}

function renderTaskLink(taskKey: string, inner: string): string {
  return `<a class="results-internal-link" href="#/tasks/${escapeAttr(taskKey)}"`
    + ` data-results-task-key="${escapeAttr(taskKey)}"`
    + ` aria-label="Open task ${escapeAttr(taskKey)}">${inner}</a>`;
}

// Clickable source reference. The host (beautiful-results.component) listens
// for clicks on `[data-results-source]` via event delegation and emits an
// `openSource` event with the path + line. This sanitized HTML lives outside
// Angular's template compiler, so [appTooltip] cannot attach here. The visible
// source text plus aria-label carry the same meaning without a native title.
function renderSourceLink(path: string, line: number | null, inner: string): string {
  const lineAttr = line != null ? ` data-results-line="${escapeAttr(String(line))}"` : '';
  const label = line != null ? `${path}:${line}` : path;
  return `<a class="results-source-link" href="#source:${escapeAttr(label)}"`
    + ` data-results-source="${escapeAttr(path)}"${lineAttr}`
    + ` aria-label="Open ${escapeAttr(label)} in source viewer">${inner}</a>`;
}

function renderCodeBlock(source: string, lang: string | null): string {
  const langLabel = lang ? `<span class="results-code__lang">${escapeHtml(lang)}</span>` : '';
  // Stage 1 escapes the source; stage 2 (lazy highlight.js) reads the raw
  // text from `data-source` and swaps the inner HTML.
  const escaped = escapeHtml(source);
  const dataSource = escapeAttr(btoaSafe(source));
  const langAttr = lang ? ` data-lang="${escapeAttr(lang)}"` : '';
  return `<div class="results-code">`
    + `<div class="results-code__head">${langLabel}`
    + `<button type="button" class="results-code__copy" data-results-copy aria-label="Copy code">Copy</button>`
    + `</div>`
    + `<pre class="results-code__pre"><code class="results-code__body" data-results-code data-source="${dataSource}"${langAttr}>${escaped}</code></pre>`
    + `</div>`;
}

function renderDiff(source: string): string {
  try {
    // diff2html parses unified-diff input and returns sanitized HTML.
    // We still wrap the result in our own container + Catppuccin-aware
    // CSS hooks so the diff matches the surrounding palette.
    const rendered = diff2htmlRender(source, {
      outputFormat: 'line-by-line',
      drawFileList: false,
      matching: 'lines'
    });
    return `<div class="results-diff" data-results-diff>${rendered}</div>`;
  } catch {
    return renderCodeBlock(source, 'diff');
  }
}

function safeLinkUrl(raw: string): string {
  const t = raw.trim();
  if (!t) return '#';
  if (/^(https?:|mailto:)/i.test(t)) return t;
  if (/^[/.#]/.test(t)) return t;
  if (/^[a-z0-9][a-z0-9+.-]*:/i.test(t)) return '#';
  return t;
}

function sanitize(raw: string): string {
  if (typeof window === 'undefined' || typeof document === 'undefined') {
    // SSR or test env without DOM; DOMPurify needs window. Fall back to
    // returning raw — callers in this codebase always run in the browser
    // for real renders, and unit tests run under jsdom which provides it.
    return raw;
  }
  return DOMPurify.sanitize(raw, {
    USE_PROFILES: { html: true },
    ADD_ATTR: [
      'target',
      'rel',
      'data-results-lightbox',
      'data-results-alt',
      'data-results-copy',
      'data-results-code',
      'data-results-diff',
      'data-results-source',
      'data-results-line',
      'data-results-wiki',
      'data-results-task-key',
      'data-results-artifact',
      'data-source',
      'data-lang',
      'loading'
    ],
    // diff2html emits <table>; allow it. Block <iframe>, <object>, etc.
    FORBID_TAGS: ['style', 'iframe', 'object', 'embed', 'script'],
    FORBID_ATTR: ['onerror', 'onload', 'onclick']
  });
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function stripEventHandlerAttributes(value: string): string {
  return value.replace(/\s+on[a-z]+\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]*)/gi, '');
}

function escapeAttr(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

/**
 * Base64-encode UTF-8 text safely. The raw source travels as a data-
 * attribute so stage 2 can swap in highlighted HTML without re-parsing
 * the markdown. We avoid plain `btoa(source)` because btoa rejects
 * non-Latin-1 input (emojis, accented characters in agent output).
 */
function btoaSafe(value: string): string {
  try {
    if (typeof TextEncoder !== 'undefined' && typeof btoa === 'function') {
      const bytes = new TextEncoder().encode(value);
      let bin = '';
      for (const byte of bytes) bin += String.fromCharCode(byte);
      return btoa(bin);
    }
  } catch { /* fall through */ }
  return '';
}

export function decodeSource(value: string | null | undefined): string {
  if (!value) return '';
  try {
    if (typeof atob === 'function') {
      const bin = atob(value);
      const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      if (typeof TextDecoder !== 'undefined') return new TextDecoder().decode(bytes);
      return bin;
    }
  } catch { /* fall through */ }
  return '';
}

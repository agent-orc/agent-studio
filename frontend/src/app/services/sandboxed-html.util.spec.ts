import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ConfirmDialogOptions } from '../models/app-dialog.model';
import {
  ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE,
  ISOLATED_HTML_ANCHORS_READY_MESSAGE,
  ISOLATED_HTML_LINK_MESSAGE,
  ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE,
  ISOLATED_HTML_TRACK_ANCHORS_MESSAGE,
  WORKBENCH_DECISION_CHANGE_MESSAGE,
  WORKBENCH_DECISION_HYDRATE_MESSAGE,
  buildIsolatedHtmlSrcdoc,
  focusScrollRegions,
  openIsolatedHtmlExternalLink,
  resolveIsolatedHtmlNavigation,
} from './sandboxed-html.util';

describe('sandboxed HTML navigation', () => {
  it('delegates non-anchor links to the host while keeping anchors in the frame', () => {
    const srcdoc = buildIsolatedHtmlSrcdoc(
      '<a href="#local">Local</a><a href="../target/index.html">Target</a><h2 id="local">Local</h2>',
    );

    expect(srcdoc).toContain(`type: '${ISOLATED_HTML_LINK_MESSAGE}'`);
    expect(srcdoc).toContain("href.charAt(0) === '#'");
    expect(srcdoc).toContain('parent.postMessage');
    expect(srcdoc).toContain(ISOLATED_HTML_ANCHORS_READY_MESSAGE);
    expect(srcdoc).toContain(ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE);
    expect(srcdoc).toContain(ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE);
    expect(srcdoc).toContain(ISOLATED_HTML_TRACK_ANCHORS_MESSAGE);
    expect(srcdoc).toContain("reduceMotion ? 'auto' : 'smooth'");
  });

  it('adds the inert Dossier decision bridge only when the host requests it', () => {
    const html = '<p data-decision-id="route" data-decision-kind="single"><span data-option-id="direct">Direct</span></p>';
    const ordinary = buildIsolatedHtmlSrcdoc(html);
    const workbench = buildIsolatedHtmlSrcdoc(html, { workbenchDecisions: true });

    expect(ordinary).not.toContain(WORKBENCH_DECISION_CHANGE_MESSAGE);
    expect(workbench).toContain(WORKBENCH_DECISION_CHANGE_MESSAGE);
    expect(workbench).toContain(WORKBENCH_DECISION_HYDRATE_MESSAGE);
    expect(workbench).toContain('data-studio-decision-control');
  });

  it('uses the descriptor pattern as the authoritative article variant', () => {
    const ui = new DOMParser().parseFromString(
      buildIsolatedHtmlSrcdoc(
        '<html data-document-pattern="concept"><body></body></html>',
        { documentPattern: 'ui' },
      ),
      'text/html',
    );
    const fallback = new DOMParser().parseFromString(
      buildIsolatedHtmlSrcdoc('<html data-document-pattern="ui"><body></body></html>'),
      'text/html',
    );

    expect(ui.documentElement.dataset['documentPattern']).toBe('ui');
    expect(fallback.documentElement.dataset['documentPattern']).toBe('concept');
  });

  it('selects authored color-scheme rules for the Studio theme', () => {
    const html = '<style>@media (prefers-color-scheme: dark) { body { color: white; } }'
      + '@media (prefers-color-scheme: light) { body { color: black; } }</style>';
    const dark = buildIsolatedHtmlSrcdoc(html, { colorScheme: 'dark' });
    const light = buildIsolatedHtmlSrcdoc(html, { colorScheme: 'light' });

    expect(dark).toContain('color-scheme: dark');
    expect(dark).toContain('@media (min-width: 0) { body { color: white; } }');
    expect(dark).toContain('@media (min-width: 999999px) { body { color: black; } }');
    expect(light).toContain('color-scheme: light');
    expect(light).toContain('@media (min-width: 0) { body { color: black; } }');
  });

  it('rewrites relative <img> sources via resolveAssetSrc and widens the CSP to the host origin', () => {
    const html = '<img src="assets/foo.png" alt="Foo"><img src="https://cdn.example/bar.png" alt="Bar">';
    const srcdoc = buildIsolatedHtmlSrcdoc(html, {
      resolveAssetSrc: (src) =>
        src.startsWith('assets/')
          ? `/api/projects/Demo/wiki/assets/operations/timeline-redesign/${src}`
          : src,
    });

    const expectedSrc = new URL(
      '/api/projects/Demo/wiki/assets/operations/timeline-redesign/assets/foo.png',
      window.location.origin,
    ).href;
    expect(srcdoc).toContain(`src="${expectedSrc}"`);
    expect(srcdoc).toContain('src="https://cdn.example/bar.png"');
    expect(srcdoc).toContain(`img-src data: ${window.location.origin};`);
  });

  it('leaves <img> sources and the CSP unchanged when no asset resolver is supplied', () => {
    const srcdoc = buildIsolatedHtmlSrcdoc('<img src="assets/foo.png" alt="Foo">');

    expect(srcdoc).toContain('src="assets/foo.png"');
    expect(srcdoc).toContain('img-src data:;');
  });

  it('resolves docs-relative targets and rejects paths that escape docs', () => {
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      '../umsetzungsplan-zielbild/index.html',
    )).toEqual({
      kind: 'wiki',
      relPath: 'operations/umsetzungsplan-zielbild/index.html',
    });
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      '../../../secrets.txt',
    )).toBeNull();
    expect(resolveIsolatedHtmlNavigation(
      'outside-docs/index.html',
      './target.html',
    )).toBeNull();
  });

  it('classifies absolute HTTP(S) links as external and rejects active schemes', () => {
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      'https://example.com/reference?q=1',
    )).toEqual({
      kind: 'external',
      url: 'https://example.com/reference?q=1',
    });
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      'javascript:alert(1)',
    )).toBeNull();
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      'mailto:docs@example.com',
    )).toBeNull();
  });
});

describe('Dossier decision controls in the isolated frame', () => {
  // The DEMO-W2 seed shape: a heading, then list options with a bold label and a description.
  const point = '<div data-decision-id="window" data-decision-kind="single">'
    + '<h3>How long are exports kept?</h3><ul>'
    + '<li data-option-id="window-7"><b>7 days</b>: smallest footprint.</li>'
    + '<li data-option-id="window-30"><b>30 days (recommended)</b>: covers a billing cycle.</li>'
    + '</ul><label>Optional note <textarea data-comment="Note"></textarea></label></div>'
    + '<section data-decision-id="proof" data-decision-kind="confirm">'
    + '<span data-option-id="proof-receipt"><input type="checkbox"> Keep a receipt</span></section>';

  function enhance(html: string): Document {
    const srcdoc = buildIsolatedHtmlSrcdoc(html, { workbenchDecisions: true });
    const doc = new DOMParser().parseFromString(srcdoc, 'text/html');
    const scripts = Array.from(doc.querySelectorAll('script'));
    const bridge = scripts[scripts.length - 1].textContent ?? '';
    const frameWindow = { addEventListener: () => undefined };
    const parent = { postMessage: vi.fn() };
    new Function('document', 'window', 'parent', bridge)(doc, frameWindow, parent);
    return doc;
  }

  it('makes the option text the label of its control, so a tap on the text selects it', () => {
    const doc = enhance(point);
    const option = doc.querySelector('[data-option-id="window-30"]')!;
    const label = option.querySelector('label[data-studio-decision-option]')!;
    const input = label.querySelector<HTMLInputElement>('input[data-studio-decision-control]')!;
    const text = label.querySelector<HTMLElement>('[data-studio-decision-text]')!;

    expect(label.parentElement).toBe(option);
    expect(input.type).toBe('radio');
    expect(text.textContent).toBe('30 days (recommended): covers a billing cycle.');
    text.querySelector('b')!.click();
    expect(input.checked).toBe(true);
  });

  it('keeps an authored control and wraps its text in the same label', () => {
    const doc = enhance(point);
    const option = doc.querySelector('[data-option-id="proof-receipt"]')!;
    const label = option.querySelector('label[data-studio-decision-option]')!;
    const input = option.querySelectorAll('input');

    expect(input).toHaveLength(1);
    expect(input[0].parentElement).toBe(label);
    label.querySelector<HTMLElement>('[data-studio-decision-text]')!.click();
    expect(input[0].checked).toBe(true);
  });

  it('names each decision point as a group with its question', () => {
    const doc = enhance(point);
    const single = doc.querySelector('[data-decision-id="window"]')!;
    const confirm = doc.querySelector('[data-decision-id="proof"]')!;
    const question = doc.getElementById(single.getAttribute('aria-labelledby')!);

    expect(single.getAttribute('role')).toBe('radiogroup');
    expect(question?.textContent).toBe('How long are exports kept?');
    expect(confirm.getAttribute('role')).toBe('group');
    expect(confirm.getAttribute('aria-label')).toBe('proof');
  });

  it('lays the option out as control plus one text block with a 24 px target', () => {
    const srcdoc = buildIsolatedHtmlSrcdoc(point, { workbenchDecisions: true });

    expect(srcdoc).not.toMatch(/\[data-option-id\]\s*\{[^}]*display:\s*flex/);
    expect(srcdoc).toMatch(/\[data-studio-decision-option\]\s*\{[^}]*display: flex[^}]*min-height: 24px/);
    expect(srcdoc).toMatch(/\[data-studio-decision-text\]\s*\{[^}]*display: block/);
  });
});

describe('external links from isolated HTML frames', () => {
  afterEach(() => vi.restoreAllMocks());

  function dialogAnswering(answer: boolean) {
    const asked: ConfirmDialogOptions[] = [];
    return {
      asked,
      dialog: {
        confirm: (options: ConfirmDialogOptions) => {
          asked.push(options);
          return Promise.resolve(answer);
        },
      },
    };
  }

  it('names the target host in the dialog and opens the page only after confirmation', async () => {
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    const { dialog, asked } = dialogAnswering(true);

    await expect(openIsolatedHtmlExternalLink(dialog, 'https://example.com:8443/reference?q=1'))
      .resolves.toBe(true);

    expect(asked).toEqual([{
      title: 'Open example.com:8443?',
      message: 'This link leaves Agent Studio and opens a page on example.com:8443 in a new tab.',
      detail: 'https://example.com:8443/reference?q=1',
      confirmLabel: 'Open page',
      kind: 'primary',
    }]);
    expect(open).toHaveBeenCalledWith('https://example.com:8443/reference?q=1', '_blank', 'noopener,noreferrer');
  });

  it('opens nothing when the operator cancels', async () => {
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    const { dialog } = dialogAnswering(false);

    await expect(openIsolatedHtmlExternalLink(dialog, 'https://example.com/')).resolves.toBe(false);

    expect(open).not.toHaveBeenCalled();
  });

  it('shows the host the browser will contact, not the credentials or Unicode form in the link', async () => {
    vi.spyOn(window, 'open').mockImplementation(() => null);
    const { dialog, asked } = dialogAnswering(true);

    await openIsolatedHtmlExternalLink(dialog, 'https://studio.example@b\u00fccher.example/login');

    expect(asked[0].title).toBe('Open xn--bcher-kva.example?');
  });

  it('asks nothing for a URL that is not HTTP(S)', async () => {
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    const { dialog, asked } = dialogAnswering(true);

    await expect(openIsolatedHtmlExternalLink(dialog, 'javascript:alert(1)')).resolves.toBe(false);
    await expect(openIsolatedHtmlExternalLink(dialog, 'not a url')).resolves.toBe(false);

    expect(asked).toEqual([]);
    expect(open).not.toHaveBeenCalled();
  });
});

describe('scroll regions in isolated HTML (AGT-3835)', () => {
  function scrolling(el: HTMLElement, scrolls: boolean): void {
    Object.defineProperty(el, 'clientWidth', { configurable: true, value: 300 });
    Object.defineProperty(el, 'scrollWidth', { configurable: true, value: scrolls ? 600 : 300 });
  }

  it('makes a scrolling table focusable and releases it once it fits', () => {
    document.body.innerHTML = '<table style="display: block; overflow-x: auto"><tr><td>wide</td></tr></table>'
      + '<pre style="overflow-x: auto"><a href="#x">link</a></pre><p>text</p>';
    const table = document.querySelector('table')!;
    const pre = document.querySelector('pre')!;
    scrolling(table, true);
    scrolling(pre, true);

    focusScrollRegions(document);
    expect(table.getAttribute('tabindex')).toBe('0');
    expect(pre.hasAttribute('tabindex')).toBe(false);
    expect(document.querySelector('p')!.hasAttribute('tabindex')).toBe(false);

    scrolling(table, false);
    focusScrollRegions(document);
    expect(table.hasAttribute('tabindex')).toBe(false);
  });

  it('runs inside every isolated frame', () => {
    expect(buildIsolatedHtmlSrcdoc('<p>x</p>')).toContain('data-studio-scroll-focus');
  });
});

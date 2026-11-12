import { describe, it, expect } from 'vitest';
import { DIFF2HTML_STYLESHEET, loadDiff2HtmlStylesheet } from './diff2html-lazy';

/** A detached page whose head mirrors the production index.html. */
function pageWithAppStylesheet(): Document {
  const doc = document.implementation.createHTMLDocument('studio');
  doc.head.innerHTML = '<link rel="stylesheet" href="styles-ABC123.css"><style id="component"></style>';
  return doc;
}

function diff2htmlLinks(doc: Document): HTMLLinkElement[] {
  return Array.from(doc.querySelectorAll<HTMLLinkElement>(`link[href="${DIFF2HTML_STYLESHEET}"]`));
}

describe('loadDiff2HtmlStylesheet', () => {
  it('links the stylesheet directly after the app stylesheet, ahead of component styles', () => {
    const doc = pageWithAppStylesheet();

    void loadDiff2HtmlStylesheet(doc);

    const [link] = diff2htmlLinks(doc);
    expect(link.rel).toBe('stylesheet');
    expect(link.previousElementSibling?.getAttribute('href')).toBe('styles-ABC123.css');
    expect(link.nextElementSibling?.id).toBe('component');
  });

  it('resolves once the sheet loads and links it only once', async () => {
    const doc = pageWithAppStylesheet();

    const first = loadDiff2HtmlStylesheet(doc);
    const second = loadDiff2HtmlStylesheet(doc);
    diff2htmlLinks(doc)[0].dispatchEvent(new Event('load'));

    await expect(first).resolves.toBeUndefined();
    expect(second).toBe(first);
    expect(diff2htmlLinks(doc)).toHaveLength(1);
  });

  it('still resolves when the sheet fails to load, so diffs keep rendering', async () => {
    const doc = pageWithAppStylesheet();

    const load = loadDiff2HtmlStylesheet(doc);
    diff2htmlLinks(doc)[0].dispatchEvent(new Event('error'));

    await expect(load).resolves.toBeUndefined();
  });

  it('links nothing on a page without the app stylesheet, such as the unit-test build', async () => {
    const doc = document.implementation.createHTMLDocument('bare');

    await expect(loadDiff2HtmlStylesheet(doc)).resolves.toBeUndefined();
    expect(diff2htmlLinks(doc)).toHaveLength(0);
  });
});

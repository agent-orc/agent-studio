import { afterEach, describe, expect, it, vi } from 'vitest';
import { RenderedReferenceHydrator } from './rendered-reference-hydrator';

class ProbeHydrator extends RenderedReferenceHydrator {
  scans = 0;
  cleanups = 0;

  roots(selector: string): HTMLElement[] {
    return this.collectRoots(selector);
  }

  nodes(root: HTMLElement): Text[] {
    return this.textNodes(root, node => !node.parentElement?.closest('code'));
  }

  frameStyles(frameDocument: Document): void {
    this.prepareFrameStyles(frameDocument, 'probeStyles', {
      copyBodyClass: true,
      copyStudioTheme: true,
    });
  }

  protected override scan(): void {
    this.scans++;
  }

  protected override cleanup(): void {
    this.cleanups++;
  }
}

afterEach(() => {
  document.body.replaceChildren();
  document.head.querySelectorAll('[data-probe="true"]').forEach(node => node.remove());
  document.documentElement.className = '';
  delete document.documentElement.dataset['studioTheme'];
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('RenderedReferenceHydrator', () => {
  it('starts an observer and schedules scans after document mutations', async () => {
    vi.useFakeTimers();
    let notify: MutationCallback | undefined;
    const observe = vi.fn();
    class MutationObserverStub {
      constructor(callback: MutationCallback) {
        notify = callback;
      }
      observe = observe;
    }
    vi.stubGlobal('MutationObserver', MutationObserverStub);
    const hydrator = new ProbeHydrator();

    hydrator.start();
    await Promise.resolve();
    vi.advanceTimersByTime(60);
    expect(hydrator.scans).toBe(1);

    notify!([], {} as MutationObserver);
    vi.advanceTimersByTime(60);
    expect(hydrator.cleanups).toBe(1);
    expect(hydrator.scans).toBe(2);
    expect(observe).toHaveBeenCalledOnce();
  });

  it('collects rendered-document roots and filters their text nodes', () => {
    document.body.innerHTML = '<cac-markdown>Keep <code>skip</code></cac-markdown>'
      + '<iframe data-testid="project-wiki-html-frame"></iframe>';
    const frame = document.querySelector('iframe') as HTMLIFrameElement;
    frame.contentDocument!.body.innerHTML = '<p>Frame text</p>';
    const hydrator = new ProbeHydrator();
    const roots = hydrator.roots('cac-markdown');

    expect(roots).toHaveLength(2);
    expect(roots.flatMap(root => hydrator.nodes(root)).map(node => node.textContent))
      .toEqual(['Keep ', 'Frame text']);
  });

  it('copies host styles and theme markers into an iframe once', () => {
    document.head.insertAdjacentHTML('beforeend', '<style data-probe="true">.probe { color: inherit; }</style>');
    document.documentElement.className = 'theme-host';
    document.documentElement.dataset['studioTheme'] = 'dark';
    document.body.className = 'body-host';
    const frame = document.createElement('iframe');
    document.body.append(frame);
    const frameDocument = frame.contentDocument!;
    const hydrator = new ProbeHydrator();

    hydrator.frameStyles(frameDocument);
    hydrator.frameStyles(frameDocument);

    expect(frameDocument.head.dataset['probeStyles']).toBe('true');
    expect(frameDocument.head.querySelectorAll('style[data-probe="true"]')).toHaveLength(1);
    expect(frameDocument.documentElement.className).toBe('theme-host');
    expect(frameDocument.documentElement.dataset['studioTheme']).toBe('dark');
    expect(frameDocument.body.className).toBe('body-host');
  });
});

/**
 * `extraImgOrigin` lets the host's own origin serve `img-src` requests, so
 * artifact `<img>` references rewritten to the wiki assets API (see
 * `resolveAssetSrc` on `buildIsolatedHtmlSrcdoc`) can actually load; the
 * sandboxed iframe has an opaque origin, so `'self'` would not match it.
 */
export function buildIsolatedHtmlCsp(extraImgOrigin?: string): string {
  const imgSrc = extraImgOrigin ? `data: ${extraImgOrigin}` : 'data:';
  return "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
    `img-src ${imgSrc}; font-src data:; connect-src 'none'; media-src data:; ` +
    "object-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; " +
    "form-action 'none'; base-uri 'none'";
}

export const ISOLATED_HTML_CSP = buildIsolatedHtmlCsp();

export const ISOLATED_HTML_LINK_MESSAGE = 'agent-studio:isolated-html-link';
export const ISOLATED_HTML_ANCHORS_READY_MESSAGE = 'agent-studio:isolated-html-anchors-ready';
export const ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE = 'agent-studio:isolated-html-active-anchor';
export const ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE = 'agent-studio:isolated-html-scroll-anchor';
export const ISOLATED_HTML_TRACK_ANCHORS_MESSAGE = 'agent-studio:isolated-html-track-anchors';
export const WORKBENCH_DECISION_READY_MESSAGE = 'agent-studio:workbench-decision-ready';
export const WORKBENCH_DECISION_CHANGE_MESSAGE = 'agent-studio:workbench-decision-change';
export const WORKBENCH_DECISION_HYDRATE_MESSAGE = 'agent-studio:workbench-decision-hydrate';
export const WORKBENCH_DECISION_ANCHOR_MESSAGE = 'agent-studio:workbench-decision-anchor';
export const WORKBENCH_DECISION_HOST_HEIGHT_MESSAGE = 'agent-studio:workbench-decision-host-height';
export const WORKBENCH_DECISION_FOCUS_ACTION_MESSAGE = 'agent-studio:workbench-decision-focus-action';

export type IsolatedHtmlNavigation =
  | { kind: 'wiki'; relPath: string }
  | { kind: 'external'; url: string };

/**
 * Wrap repository-authored HTML behind a policy-first document. DOMParser is
 * inert, so artifact scripts do not execute until the fixed wrapper reaches an
 * iframe with `sandbox="allow-scripts"`. The missing `allow-same-origin`
 * capability keeps the resulting document on an opaque origin.
 */
export function buildIsolatedHtmlSrcdoc(
  html: string,
  options: {
    workbenchDecisions?: boolean;
    documentPattern?: 'ui' | 'concept';
    /**
     * Rewrites an artifact-authored `<img src>` (relative to the dossier/wiki
     * doc's own folder, e.g. `assets/foo.png`) to a loadable URL, typically
     * the wiki assets API via `resolveWikiImageSrc`. Without this, sibling
     * assets 404 because the frame's base is forced to `about:blank`.
     */
    resolveAssetSrc?: (src: string) => string;
  } = {},
): string {
  if (!html) return '';
  const parser = new DOMParser();
  const artifact = parser.parseFromString(html, 'text/html');
  const wrapper = parser.parseFromString(
    '<!doctype html><html><head></head><body></body></html>',
    'text/html',
  );

  for (const control of Array.from(artifact.querySelectorAll('base, meta'))) {
    if (isArtifactSecurityControl(control)) control.remove();
  }

  const origin = typeof window !== 'undefined' ? window.location.origin : '';
  let permitOriginImages = false;
  if (options.resolveAssetSrc) {
    const resolveAssetSrc = options.resolveAssetSrc;
    for (const image of Array.from(artifact.querySelectorAll('img[src]'))) {
      const src = image.getAttribute('src');
      if (!src) continue;
      const resolved = absolutizeAssetUrl(resolveAssetSrc(src), origin);
      if (resolved !== src) permitOriginImages = true;
      image.setAttribute('src', resolved);
    }
  }

  const policy = wrapper.createElement('meta');
  policy.httpEquiv = 'Content-Security-Policy';
  policy.content = buildIsolatedHtmlCsp(permitOriginImages ? origin : undefined);
  const base = wrapper.createElement('base');
  base.href = 'about:blank';
  wrapper.head.append(policy, base);

  copyAttributes(artifact.documentElement, wrapper.documentElement);
  // The descriptor is authoritative for the article variant. Existing HTML
  // without v2 template selectors is unaffected by this neutral data hook.
  wrapper.documentElement.setAttribute(
    'data-document-pattern', options.documentPattern === 'ui' ? 'ui' : 'concept');
  copyAttributes(artifact.head, wrapper.head);
  copyAttributes(artifact.body, wrapper.body);
  for (const node of Array.from(artifact.head.childNodes))
    wrapper.head.append(wrapper.importNode(node, true));
  for (const node of Array.from(artifact.body.childNodes))
    wrapper.body.append(wrapper.importNode(node, true));

  // `base=about:blank` also disables in-page anchors. Restore scrolling inside
  // the frame and delegate every other link to the trusted host. The host
  // verifies the sending iframe and resolves the raw href against the current
  // repository path before it performs any navigation.
  const nav = wrapper.createElement('script');
  nav.textContent = `document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a) return;
    var href = a.getAttribute('href') || '';
    e.preventDefault();
    if (href.charAt(0) === '#') {
      var id = href.slice(1);
      try { id = decodeURIComponent(id); } catch (_) { return; }
      var el = document.getElementById(id);
      if (!el) {
        var named = document.querySelectorAll('a[name]');
        for (var i = 0; i < named.length; i += 1) {
          if (named[i].getAttribute('name') === id) { el = named[i]; break; }
        }
      }
      var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      if (el) el.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' });
      return;
    }
    parent.postMessage({ type: '${ISOLATED_HTML_LINK_MESSAGE}', href: href }, '*');
  }, true);`;
  wrapper.body.append(nav);

  const anchorBridge = wrapper.createElement('script');
  anchorBridge.textContent = isolatedHtmlAnchorBridgeScript();
  wrapper.body.append(anchorBridge);

  if (options.workbenchDecisions) {
    const style = wrapper.createElement('style');
    style.textContent = `
      [data-studio-decision-enhanced] [data-option-id] {
        display: flex !important;
        align-items: flex-start !important;
        gap: .55em !important;
      }
      [data-studio-decision-control] {
        width: 1em !important;
        height: 1em !important;
        min-width: 1em !important;
        margin: .2em 0 0 !important;
        accent-color: AccentColor !important;
      }
      [data-studio-decision-comment] {
        display: block !important;
        width: 100% !important;
        min-height: 3.4em !important;
        box-sizing: border-box !important;
        margin-top: .65em !important;
        border: 1px solid color-mix(in srgb, currentColor 28%, transparent) !important;
        border-radius: .45em !important;
        background: Canvas !important;
        padding: .55em .65em !important;
        color: CanvasText !important;
        font: inherit !important;
        resize: vertical !important;
      }
      [data-studio-decision-control]:focus-visible,
      [data-studio-decision-comment]:focus-visible {
        outline: 2px solid AccentColor !important;
        outline-offset: 2px !important;
      }
      @media (prefers-reduced-motion: reduce) {
        [data-studio-decision-enhanced] * { scroll-behavior: auto !important; }
      }`;
    wrapper.head.append(style);
    const decisions = wrapper.createElement('script');
    decisions.textContent = workbenchDecisionBridgeScript();
    wrapper.body.append(decisions);
  }

  return `<!doctype html>${wrapper.documentElement.outerHTML}`;
}

function isolatedHtmlAnchorBridgeScript(): string {
  return `(function(){
var tracked=[],pending=false;
function anchorFor(id){return document.getElementById(id);}
function inventory(){
var ids=[],seen=Object.create(null);
Array.prototype.forEach.call(document.querySelectorAll('[id]'),function(element){
var id=element.id;
if(!id||seen[id])return;
seen[id]=true;
ids.push(id);
});
return ids;
}
function publishInventory(){
parent.postMessage({type:'${ISOLATED_HTML_ANCHORS_READY_MESSAGE}',anchors:inventory()},'*');
}
function activeId(){
var active=null,edge=Math.max(32,Math.min(96,window.innerHeight*.16));
for(var i=0;i<tracked.length;i+=1){
var element=anchorFor(tracked[i]);
if(!element)continue;
if(element.getBoundingClientRect().top<=edge)active=tracked[i];
else if(!active)return tracked[i];
else break;
}
return active;
}
function publishActive(){
pending=false;
parent.postMessage({type:'${ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE}',id:activeId()},'*');
}
function scheduleActive(){
if(pending)return;
pending=true;
requestAnimationFrame(publishActive);
}
window.addEventListener('scroll',scheduleActive,{passive:true});
window.addEventListener('message',function(event){
if(event.source!==parent)return;
var message=event.data;
if(!message||typeof message.type!=='string')return;
if(message.type==='${ISOLATED_HTML_TRACK_ANCHORS_MESSAGE}'&&Array.isArray(message.ids)){
tracked=message.ids.filter(function(id,index,ids){
return typeof id==='string'&&id.length<=512&&ids.indexOf(id)===index;
});
scheduleActive();
return;
}
if(message.type!=='${ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE}'||typeof message.id!=='string')return;
var target=anchorFor(message.id);
if(!target){publishInventory();return;}
var reduceMotion=window.matchMedia&&window.matchMedia('(prefers-reduced-motion: reduce)').matches;
var behavior=reduceMotion?'auto':'smooth';
target.scrollIntoView({behavior:behavior,block:'start'});
scheduleActive();
if(behavior==='smooth')setTimeout(function(){
if(Math.abs(target.getBoundingClientRect().top)>96)target.scrollIntoView({behavior:'auto',block:'start'});
scheduleActive();
},500);
});
publishInventory();
})();`;
}

function workbenchDecisionBridgeScript(): string {
  return `(function () {
    var safeId = /^[A-Za-z0-9_-]{1,80}$/;
    var kinds = { single: true, multi: true, confirm: true };
    var points = [];
    var seen = Object.create(null);
    Array.prototype.forEach.call(document.querySelectorAll('[data-decision-id][data-decision-kind]'), function (point) {
      var id = (point.getAttribute('data-decision-id') || '').trim();
      var kind = (point.getAttribute('data-decision-kind') || '').trim();
      if (!safeId.test(id) || !kinds[kind] || seen[id]) return;
      var optionIds = Object.create(null);
      var options = [];
      Array.prototype.forEach.call(point.querySelectorAll('[data-option-id]'), function (option) {
        var optionId = (option.getAttribute('data-option-id') || '').trim();
        if (!safeId.test(optionId) || optionIds[optionId]) return;
        optionIds[optionId] = true;
        var input = option.querySelector('input[type="checkbox"],input[type="radio"]');
        if (!input) {
          input = document.createElement('input');
          option.insertBefore(input, option.firstChild);
        }
        input.type = kind === 'single' ? 'radio' : 'checkbox';
        if (kind === 'single') input.name = 'studio-decision-' + id;
        input.value = optionId;
        input.setAttribute('data-studio-decision-control', '');
        input.setAttribute('aria-label', (option.getAttribute('data-option-label') || option.textContent || optionId).trim());
        options.push({ id: optionId, input: input });
      });
      if (!options.length) return;
      var marker = point.querySelector('[data-comment]');
      var comment = null;
      if (marker) {
        if (marker.matches('textarea,input[type="text"]')) comment = marker;
        else comment = marker.querySelector('textarea,input[type="text"]');
        if (!comment) {
          comment = document.createElement('textarea');
          marker.appendChild(comment);
        }
        var commentLabel = (marker.getAttribute('data-comment') || marker.getAttribute('aria-label')
          || marker.getAttribute('placeholder') || marker.textContent || 'Optional comment').trim();
        comment.setAttribute('aria-label', commentLabel);
        if (!comment.getAttribute('placeholder')) comment.setAttribute('placeholder', commentLabel);
        comment.setAttribute('data-studio-decision-comment', '');
      }
      point.setAttribute('data-studio-decision-enhanced', '');
      seen[id] = true;
      points.push({ id: id, kind: kind, root: point, options: options, comment: comment });
    });

    var hostSlot = null;
    var anchorPoint = points.length ? points[points.length - 1] : null;
    var anchorPending = false;
    if (anchorPoint) {
      hostSlot = document.createElement('div');
      hostSlot.setAttribute('data-studio-decision-host-slot', '');
      hostSlot.setAttribute('aria-hidden', 'true');
      hostSlot.style.cssText = 'display:block!important;height:72px!important;min-height:72px!important;pointer-events:none!important;';
      anchorPoint.root.insertAdjacentElement('afterend', hostSlot);
    }

    function publishAnchor() {
      anchorPending = false;
      if (!hostSlot || !anchorPoint) return;
      var rect = hostSlot.getBoundingClientRect();
      parent.postMessage({
        type: '${WORKBENCH_DECISION_ANCHOR_MESSAGE}',
        decisionId: anchorPoint.id,
        rect: { top: rect.top, left: rect.left, width: rect.width, height: rect.height },
        visible: rect.bottom > 0 && rect.top < window.innerHeight && rect.right > 0 && rect.left < window.innerWidth
      }, '*');
    }
    function scheduleAnchor() {
      if (anchorPending) return;
      anchorPending = true;
      requestAnimationFrame(publishAnchor);
    }
    window.addEventListener('scroll', scheduleAnchor, { passive: true });
    window.addEventListener('resize', scheduleAnchor, { passive: true });
    if (anchorPoint && typeof ResizeObserver === 'function')
      new ResizeObserver(scheduleAnchor).observe(anchorPoint.root);

    document.addEventListener('keydown', function (event) {
      if (!anchorPoint || event.key !== 'Tab' || event.shiftKey || event.altKey || event.ctrlKey || event.metaKey) return;
      var controls = anchorPoint.options.map(function (option) { return option.input; });
      if (anchorPoint.comment) controls.push(anchorPoint.comment);
      if (controls.length && event.target === controls[controls.length - 1]) {
        event.preventDefault();
        parent.postMessage({ type: '${WORKBENCH_DECISION_FOCUS_ACTION_MESSAGE}' }, '*');
      }
    }, true);

    function read() {
      return points.map(function (point) {
        return {
          decisionId: point.id,
          kind: point.kind,
          selectedOptionIds: point.options.filter(function (option) { return option.input.checked; })
            .map(function (option) { return option.id; }),
          comment: point.comment && point.comment.value.trim() ? point.comment.value.trim().slice(0, 20000) : null
        };
      });
    }
    function publish(type) { parent.postMessage({ type: type, responses: read() }, '*'); }
    document.addEventListener('change', function (event) {
      if (event.target && event.target.hasAttribute && event.target.hasAttribute('data-studio-decision-control'))
        publish('${WORKBENCH_DECISION_CHANGE_MESSAGE}');
    }, true);
    document.addEventListener('input', function (event) {
      if (event.target && event.target.hasAttribute && event.target.hasAttribute('data-studio-decision-comment'))
        publish('${WORKBENCH_DECISION_CHANGE_MESSAGE}');
    }, true);
    window.addEventListener('message', function (event) {
      var message = event.data;
      if (message && message.type === '${WORKBENCH_DECISION_HOST_HEIGHT_MESSAGE}' && hostSlot) {
        var height = Number(message.height);
        if (isFinite(height) && height >= 48 && height <= 800) {
          hostSlot.style.setProperty('height', Math.ceil(height) + 'px', 'important');
          hostSlot.style.setProperty('min-height', Math.ceil(height) + 'px', 'important');
          scheduleAnchor();
        }
        return;
      }
      if (!message || message.type !== '${WORKBENCH_DECISION_HYDRATE_MESSAGE}' || !Array.isArray(message.responses)) return;
      message.responses.forEach(function (response) {
        var point = points.find(function (candidate) { return candidate.id === response.decisionId; });
        if (!point || response.kind !== point.kind || !Array.isArray(response.selectedOptionIds)) return;
        point.options.forEach(function (option) {
          option.input.checked = response.selectedOptionIds.indexOf(option.id) >= 0;
          option.input.disabled = !!message.readonly;
        });
        if (point.comment) {
          point.comment.value = typeof response.comment === 'string' ? response.comment.slice(0, 20000) : '';
          point.comment.disabled = !!message.readonly;
        }
      });
    });
    scheduleAnchor();
    publish('${WORKBENCH_DECISION_READY_MESSAGE}');
  })();`;
}

/**
 * Resolve a link reported by an isolated HTML frame without granting that
 * frame browser-navigation capability. Only repository-relative paths that
 * remain under `docs/` may enter the Wiki; absolute HTTP(S) links are external.
 */
export function resolveIsolatedHtmlNavigation(
  entryPath: string,
  href: string,
): IsolatedHtmlNavigation | null {
  const target = href.trim();
  if (!target || target.startsWith('#')) return null;

  if (/^https?:\/\//i.test(target) || target.startsWith('//')) {
    try {
      const url = new URL(target, 'https://external.invalid/');
      return url.protocol === 'http:' || url.protocol === 'https:'
        ? { kind: 'external', url: url.href }
        : null;
    } catch {
      return null;
    }
  }
  if (/^[a-z][a-z0-9+.-]*:/i.test(target)) return null;

  const cleanEntryPath = entryPath.trim().replaceAll('\\', '/').replace(/^\/+/, '');
  if (!cleanEntryPath.startsWith('docs/')) return null;
  try {
    const resolved = new URL(target.replaceAll('\\', '/'), `https://repository.invalid/${cleanEntryPath}`);
    if (resolved.origin !== 'https://repository.invalid') return null;
    const decodedPath = decodeURIComponent(resolved.pathname).replace(/^\/+/, '');
    if (!decodedPath.startsWith('docs/')) return null;
    const relPath = decodedPath.slice('docs/'.length);
    return relPath ? { kind: 'wiki', relPath } : null;
  } catch {
    return null;
  }
}

/**
 * The frame's forced `about:blank` base makes root-relative URLs (the wiki
 * assets API returns `/api/...`) resolve unpredictably. Qualifying them
 * against the host's own origin up front sidesteps that entirely; already
 * absolute or `data:` URLs pass through `new URL` unchanged.
 */
function absolutizeAssetUrl(src: string, origin: string): string {
  if (!origin) return src;
  try {
    return new URL(src, origin).href;
  } catch {
    return src;
  }
}

function copyAttributes(source: Element, target: Element): void {
  for (const attribute of Array.from(source.attributes))
    target.setAttribute(attribute.name, attribute.value);
}

function isArtifactSecurityControl(node: Node): boolean {
  if (!(node instanceof HTMLBaseElement || node instanceof HTMLMetaElement)) return false;
  if (node instanceof HTMLBaseElement) return true;
  const httpEquiv = node.httpEquiv.trim().toLowerCase();
  return httpEquiv === 'content-security-policy'
    || httpEquiv === 'content-security-policy-report-only'
    || httpEquiv === 'refresh';
}

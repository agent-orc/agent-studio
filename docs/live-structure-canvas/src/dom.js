// DOM helpers of the prototype: an element builder and the legend swatches.
(function () {
  'use strict';
  const el = (tag, attrs, ...kids) => {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v == null || v === false) continue;
      if (k === 'class') e.className = v; else if (k === 'text') e.textContent = v; else if (k.startsWith('on')) e.addEventListener(k.slice(2), v); else e.setAttribute(k, v === true ? '' : v);
    }
    for (const c of kids.flat()) if (c != null) e.append(c.nodeType ? c : document.createTextNode(String(c)));
    return e;
  };
  function swatch(kind, tone, dash) {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('width', '26'); svg.setAttribute('height', '14'); svg.setAttribute('aria-hidden', 'true');
    const c = `var(--lsc-${tone})`;
    const add = (tag, a) => { const e = document.createElementNS(ns, tag); for (const [k, v] of Object.entries(a)) e.setAttribute(k, v); svg.append(e); return e; };
    if (kind === 'bar') { add('rect', { x: 1, y: 5, width: 24, height: 5, fill: 'var(--lsc-surface-strong)' }); add('rect', { x: 1, y: 5, width: 15, height: 5, fill: c }); }
    else if (kind === 'dots') for (let i = 0; i < 4; i++) add('circle', { cx: 3 + i * 6.5, cy: 7, r: 2, fill: c });
    else if (kind === 'line') add('line', { x1: 1, y1: 7, x2: 25, y2: 7, stroke: c, 'stroke-width': 1.8, 'stroke-dasharray': (dash || []).join(' ') });
    else if (kind === 'broken') { add('line', { x1: 1, y1: 7, x2: 10, y2: 7, stroke: c, 'stroke-width': 1.8 }); add('line', { x1: 16, y1: 7, x2: 25, y2: 7, stroke: c, 'stroke-width': 1.8 }); add('path', { d: 'M11 4 L15 10 M15 4 L11 10', stroke: c, 'stroke-width': 1.6 }); }
    else if (kind === 'shield') add('path', { d: 'M13 1 L19 3.5 L19 7.5 Q18 11.5 13 13 Q8 11.5 7 7.5 L7 3.5 Z', fill: tone === 'muted' || tone === 'faint' ? 'none' : c, stroke: c, 'stroke-width': 1.2, 'stroke-dasharray': tone === 'faint' ? '2 2' : '' });
    else if (kind === 'pin') { add('path', { d: 'M13 1 L18.5 11 L7.5 11 Z', fill: c }); add('rect', { x: 6, y: 10.5, width: 14, height: 2.5, fill: c }); }
    else if (kind === 'dot') add('circle', { cx: 13, cy: 7, r: 4, fill: c });
    else if (kind === 'ring') add('circle', { cx: 13, cy: 7, r: 3.6, fill: 'none', stroke: c, 'stroke-width': 1.4 });
    else if (kind === 'chip') add('rect', { x: 2, y: 3, width: 22, height: 8, rx: 4, fill: tone === 'warn' ? 'var(--lsc-warn-soft)' : 'var(--lsc-surface-strong)', stroke: c, 'stroke-width': 0.8 });
    else if (kind === 'outline') add('rect', { x: 2, y: 2, width: 22, height: 10, rx: 3, fill: 'none', stroke: c, 'stroke-width': 1.4, 'stroke-dasharray': (dash || []).join(' ') });
    else if (kind === 'text') { const t = add('text', { x: 1, y: 11, 'font-size': 9, fill: c }); t.textContent = '21 d'; }
    return svg;
  }
  window.LSCDom = { el, swatch };
})();

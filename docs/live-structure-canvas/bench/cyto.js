// Candidate C: Cytoscape.js 3 (graph framework with compound nodes).
// Preset positions from the shared layout; Cytoscape renders on demand.
import cytoscape from 'cytoscape';
import { install } from './harness.js';

let cy, flowOn = false, frames = 0, offset = 0, timer = 0;
install('cytoscape', {
  init(host, scene, model, { W, H }) {
    const div = document.createElement('div');
    div.style.width = W + 'px'; div.style.height = H + 'px';
    host.appendChild(div);
    const els = [];
    for (const n of scene.nodes) {
      const leaf = n.kind === 'container';
      els.push({ data: { id: n.id, parent: n.parent || undefined, label: n.label, leaf: leaf ? 1 : 0 }, position: leaf ? { x: n.x + n.w / 2, y: n.y + n.h / 2 } : undefined });
    }
    for (const l of scene.links) els.push({ data: { id: l.id, source: l.from, target: l.to } });
    cy = cytoscape({
      container: div, elements: els, layout: { name: 'preset' }, pixelRatio: 1, autoungrabify: true,
      userZoomingEnabled: false, userPanningEnabled: false, boxSelectionEnabled: false,
      style: [
        { selector: 'node', style: { shape: 'round-rectangle', width: 180, height: 116, 'background-color': '#fcfcfb', 'border-width': 1, 'border-color': '#d8d6cf', label: 'data(label)', 'font-size': 13.5, color: '#151515', 'text-valign': 'top', 'text-halign': 'center', 'text-margin-y': 22 } },
        { selector: ':parent', style: { 'background-color': '#f2f1ee', padding: 20, 'text-valign': 'top', 'text-margin-y': 0 } },
        { selector: 'edge', style: { 'curve-style': 'bezier', width: 1.1, 'line-color': '#5f5e59', 'line-style': 'solid' } },
        { selector: 'edge.flow', style: { 'line-style': 'dashed', 'line-dash-pattern': [2, 10] } },
      ],
    });
    cy.on('render', () => { frames++; });
  },
  camera(k, tx, ty) { cy.viewport({ zoom: k, pan: { x: tx, y: ty } }); },
  render() { /* Cytoscape draws on its own animation frame after a viewport change. */ },
  flow(on) {
    flowOn = on; clearTimeout(timer);
    // No particle primitive: the nearest equivalent is an animated dash offset on every edge.
    cy.batch(() => { if (on) cy.edges().addClass('flow'); else cy.edges().removeClass('flow'); });
    const tick = () => { if (!flowOn) return; offset = (offset + 2) % 12; cy.edges().style('line-dash-offset', -offset); timer = setTimeout(() => requestAnimationFrame(tick), 33); };
    if (on) requestAnimationFrame(tick);
  },
  frames() { return frames; },
});

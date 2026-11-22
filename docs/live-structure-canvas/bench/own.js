// Candidate A: the Dossier's own Canvas 2D engine (no dependency).
// own.js draws every node and link; own-semantic.js keeps semantic zoom on.
import engine from '../src/engine.js';
import { install } from './harness.js';

const SEMANTIC = !!globalThis.LSC_SEMANTIC;
let eng;
const tokens = { paper: '#fcfcfb', surface: '#f2f1ee', surfaceSoft: '#f7f6f3', surfaceStrong: '#e9e8e3', ink: '#151515', muted: '#5f5e59', faint: '#696762', line: '#d8d6cf', link: '#1f67b2', bad: '#b72f2f', good: '#18753a', sideA: '#d8d6cf', sideB: '#e9e8e3' };
install(SEMANTIC ? "own-canvas2d-semantic" : "own-canvas2d", {
  init(host, scene, model, { W, H }) {
    const canvas = document.createElement('canvas');
    host.appendChild(canvas);
    eng = engine.createEngine(canvas, { openAll: !SEMANTIC, motion: 'lively', hooks: { linkStyle: () => ({ flow: eng.st.flowOn, width: 1.1 }) } });
    eng.st.flowOn = false;
    eng.setTokens(tokens);
    eng.resize(W, H, 1);
    eng.setScene(scene);
  },
  camera(k, tx, ty) { eng.setCamera(k, tx, ty); },
  render() { eng.renderNow(); },
  flow(on) { eng.st.flowOn = on; eng.renderNow(); },
  frames() { return eng.st.frames; },
});

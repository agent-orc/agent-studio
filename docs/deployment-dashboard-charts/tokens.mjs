// Resolves the Studio's semantic tokens (frontend/src/styles/_tokens-*.scss) to
// plain values for both themes, so the prototype draws with the Studio's own
// colours and fonts and invents none. Used by build.mjs; prints with --print.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const styles = resolve(dirname(fileURLToPath(import.meta.url)), '../../frontend/src/styles');
export const NAMES = ['bg-editor', 'bg-surface', 'bg-sidebar', 'bg-elevated', 'bg-hover', 'bg-selected', 'border', 'border-strong', 'fg', 'fg-strong', 'fg-dim', 'fg-muted',
  'accent', 'accent-3', 'accent-3-strong', 'accent-4-strong', 'accent-success', 'accent-success-strong', 'accent-warn', 'accent-warn-strong', 'accent-6', 'accent-6-strong', 'accent-teal',
  'tooltip-bg', 'tooltip-fg', 'tooltip-border'].map(n => '--studio-' + n).concat(['--font-ui', '--font-mono']);
function declarations(text) {
  const map = new Map();
  for (const m of text.replace(/\/\/[^\n]*/g, '').replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/(--[a-z0-9-]+)\s*:\s*([^;{}]+);/g)) map.set(m[1], m[2].trim());
  return map;
}
export function studioTokens() {
  const primitives = declarations(readFileSync(resolve(styles, '_tokens-primitives.scss'), 'utf8'));
  const semantic = readFileSync(resolve(styles, '_tokens-semantic.scss'), 'utf8');
  const at = semantic.search(/^\[data-studio-theme='light'\]\s*\{/m);
  if (at < 0) throw new Error('Light theme block not found in _tokens-semantic.scss');
  const dark = new Map([...primitives, ...declarations(semantic.slice(0, at))]);
  const light = new Map([...dark, ...declarations(semantic.slice(at))]);
  const value = (map, name, depth = 0) => {
    const raw = map.get(name);
    if (raw == null) throw new Error('Unknown Studio token ' + name);
    return depth > 8 ? raw : raw.replace(/var\((--[a-z0-9-]+)\)/g, (_, n) => value(map, n, depth + 1));
  };
  const out = { dark: {}, light: {} };
  for (const n of NAMES) { out.dark[n] = value(dark, n); out.light[n] = value(light, n); }
  return out;
}
if (process.argv.includes('--print')) console.log(JSON.stringify(studioTokens(), null, 1));

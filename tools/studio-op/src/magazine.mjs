import { usage } from './args.mjs';

/** Mirrors the Studio limits (backend/Features/Magazine/MagazineModels.cs). */
export const MAX_BODY_BYTES = 256 * 1024;
export const ARTICLE_KINDS = ['digest', 'decision-brief', 'report', 'note'];
export const SOURCE_KINDS = ['card', 'dossier', 'batch'];
const FRONT_MATTER_KEYS = new Set(['scope', 'slug', 'title', 'summary', 'kind', 'sources', 'persona']);
const LIST_KEYS = new Set(['sources']);

/**
 * Splits a Markdown article into its front matter and its body. The front
 * matter is the small YAML subset an agent writes: `key: value` with plain
 * or quoted values, a folded `key: >` block, and a `sources:` list as
 * `- card:AGT-1` lines or `[card:AGT-1, batch:b-7]`. Anything else is a
 * usage error, so a typo never publishes a half-read article.
 */
export function parseArticle(text) {
  const normalized = text.replace(/^﻿/, '').replace(/\r\n/g, '\n');
  const match = normalized.match(/^---\n([\s\S]*?)\n---[ \t]*(?:\n|$)/);
  if (!match) throw usage('front-matter-missing', 'The article file starts with a --- front matter block.');
  const fields = parseFrontMatter(match[1]);
  const body = normalized.slice(match[0].length).replace(/^\n+/, '');
  return { fields, body };
}

function parseFrontMatter(block) {
  const fields = {};
  const lines = block.split('\n');
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    if (line.trim() === '' || line.trimStart().startsWith('#')) continue;
    const pair = line.match(/^([A-Za-z][A-Za-z0-9_-]*):(?:[ \t]+(.*))?$/);
    if (!pair) throw usage('front-matter-invalid', `Front matter line ${index + 2} is not "key: value": ${line.trim()}`);
    const key = pair[1];
    if (!FRONT_MATTER_KEYS.has(key))
      throw usage('front-matter-invalid', `Unknown front matter key "${key}". Allowed: ${[...FRONT_MATTER_KEYS].join(', ')}.`);
    if (Object.hasOwn(fields, key)) throw usage('front-matter-invalid', `Front matter key "${key}" appears twice.`);
    const raw = (pair[2] ?? '').trim();
    if (raw === '>' || raw === '|') {
      const block = [];
      while (index + 1 < lines.length && (/^[ \t]+\S/.test(lines[index + 1]) || lines[index + 1].trim() === '')) block.push(lines[++index].trim());
      fields[key] = raw === '>' ? block.filter(Boolean).join(' ') : block.join('\n').trim();
    } else if (raw === '' && LIST_KEYS.has(key)) {
      const items = [];
      while (index + 1 < lines.length && /^[ \t]*-[ \t]+/.test(lines[index + 1]))
        items.push(scalar(lines[++index].replace(/^[ \t]*-[ \t]+/, '')));
      fields[key] = items;
    } else if (LIST_KEYS.has(key)) {
      const inline = raw.match(/^\[(.*)\]$/);
      fields[key] = inline ? inline[1].split(',').map(item => scalar(item)).filter(Boolean) : [scalar(raw)];
    } else {
      fields[key] = scalar(raw);
    }
  }
  return fields;
}

function scalar(raw) {
  const value = raw.trim();
  if (value.length >= 2 && value.startsWith('"') && value.endsWith('"')) {
    try { return JSON.parse(value); } catch { throw usage('front-matter-invalid', `Cannot read the quoted value ${value}.`); }
  }
  if (value.length >= 2 && value.startsWith("'") && value.endsWith("'")) return value.slice(1, -1).replace(/''/g, "'");
  return value;
}

/** `card:AGT-1` becomes { kind: 'card', ref: 'AGT-1' }; Studio validates the reference itself. */
export function parseSource(text) {
  const match = String(text).match(/^([a-z]+):(.+)$/);
  if (!match || !SOURCE_KINDS.includes(match[1]))
    throw usage('front-matter-invalid', `A source is ${SOURCE_KINDS.map(kind => `${kind}:<ref>`).join(', ')}; got "${text}".`);
  return { kind: match[1], ref: match[2].trim() };
}

export function scopeOf(value) {
  const scope = String(value ?? '').trim();
  if (!/^(installation|(workspace|project):[A-Za-z0-9][A-Za-z0-9._-]*)$/.test(scope))
    throw usage('invalid-option', 'The scope is installation, workspace:<id> or project:<id>.');
  return scope;
}

export function slugOf(value) {
  const slug = String(value ?? '').trim();
  if (!/^[a-z0-9](?:[a-z0-9-]{0,78}[a-z0-9])?$/.test(slug))
    throw usage('invalid-option', 'The slug has 1 to 80 lowercase letters, digits and inner hyphens.');
  return slug;
}

/** The route of one article; the scope keeps its colon readable. */
export function articlePath(scope, slug, rest = '') {
  return `/api/magazine/${encodeURIComponent(scope).replace(/%3A/gi, ':')}/${encodeURIComponent(slug)}${rest}`;
}

// The view specification a persona returns for the deployment dashboard, and
// its validator. A specification is data: it names marks that exist in the
// facts (a tile, a flow node, a time range, a figure) and says what to stress
// and what to write next to them. It cannot carry code, markup, styles,
// colours, scales or positions; anything invalid falls back to the facts view.
// schema is exported as JSON Schema to schema/deploy-view-spec.v1.schema.json.
const PLAIN = '^[^<>]*$';
const text = max => ({ type: 'string', minLength: 1, maxLength: max, pattern: PLAIN });
const slug = { type: 'string', maxLength: 64, pattern: '^[A-Za-z0-9][A-Za-z0-9._-]*$' };
const day = { type: 'string', maxLength: 10, pattern: '^\\d{4}-\\d{2}-\\d{2}$' };
const one = (...values) => ({ type: 'string', enum: values, pattern: PLAIN });
const obj = (required, properties) => ({ type: 'object', additionalProperties: false, required, properties });
const list = (items, maxItems, minItems = 0) => ({ type: 'array', minItems, maxItems, items });
const count = max => ({ type: 'integer', minimum: 0, maximum: max });

export const VIEWS = ['overview', 'flow', 'timeline', 'figures'];
export const FIGURES = ['frequency', 'lead', 'cfr', 'mttr'];
const figure = one(...FIGURES);
// A target is a mark of one view. The view key selects the branch.
const target = { oneOf: [
  obj(['view', 'project', 'env'], { view: { const: 'overview' }, project: text(64), env: text(32) }),
  obj(['view', 'node'], { view: { const: 'flow' }, node: slug }),
  obj(['view', 'from', 'to'], { view: { const: 'timeline' }, from: day, to: day, env: text(32) }),
  obj(['view', 'figure'], { view: { const: 'figures' }, figure }),
] };

export const schema = {
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  $id: 'deploy-view-spec/v1',
  title: 'Deployment dashboard view specification',
  ...obj(['schemaVersion', 'id', 'revision', 'persona', 'createdAt', 'model', 'confidence', 'factsUsed', 'headline'], {
    schemaVersion: { const: 'deploy-view-spec/v1' },
    id: slug,
    revision: { type: 'integer', minimum: 1, maximum: 1000000 },
    persona: obj(['id', 'role', 'title'], { id: slug, role: one('operations', 'reliability', 'cost', 'security', 'quality'), title: text(60) }),
    createdAt: { type: 'string', maxLength: 30, pattern: '^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(\\.\\d+)?Z$' },
    model: obj(['capabilityClass', 'id'], { capabilityClass: one('balanced', 'frontier'), id: text(64) }),
    confidence: { type: 'number', minimum: 0, maximum: 1 },
    tokens: obj(['input', 'output'], { input: count(1000000), output: count(100000) }),
    trigger: text(120),
    factsUsed: list(text(160), 16, 1),
    headline: text(160),
    judgement: text(700),
    order: obj(['views'], { views: list(one(...VIEWS), 4, 1) }),
    figures: list(figure, 4),
    emphasis: list(obj(['target', 'weight'], { target, weight: one('strong', 'normal') }), 12),
    annotations: list(obj(['id', 'target', 'tone', 'title', 'text'], {
      id: slug, target, tone: one('bad', 'warn', 'info', 'good'), title: text(48), text: text(160), blocks: text(120), next: text(160), factRefs: list(count(15), 6),
    }), 6),
    goals: list(obj(['id', 'title', 'done', 'total'], {
      id: slug, title: text(90), done: count(10000), total: count(10000),
      steps: list(obj(['kind', 'title', 'state'], { kind: one('owner-decision', 'card', 'operation'), title: text(90), detail: text(200), state: one('open', 'proposed', 'in-progress', 'done') }), 6),
    }), 3),
  }),
};

const kind = v => (Array.isArray(v) ? 'array' : v === null ? 'null' : typeof v);
function check(s, v, at, errors) {
  if (errors.length > 24) return;
  if (s.oneOf) {
    const branch = v && typeof v === 'object' ? s.oneOf.find(b => b.properties.view.const === v.view) : null;
    if (!branch) { errors.push(`${at}.view: must be one of ${s.oneOf.map(b => b.properties.view.const).join(', ')}`); return; }
    check(branch, v, at, errors); return;
  }
  if ('const' in s) { if (v !== s.const) errors.push(`${at}: must be "${s.const}"`); return; }
  const k = kind(v), want = s.type === 'integer' ? 'number' : s.type;
  if (k !== want) { errors.push(`${at}: expected ${s.type}, found ${k}`); return; }
  if (k === 'string') {
    if (s.pattern === PLAIN && /[<>]/.test(v)) errors.push(`${at}: markup is not allowed in text`);
    else if (s.pattern && !new RegExp(s.pattern).test(v)) errors.push(`${at}: does not match ${s.pattern}`);
    if (s.minLength && v.length < s.minLength) errors.push(`${at}: is empty`);
    if (s.maxLength && v.length > s.maxLength) errors.push(`${at}: ${v.length} characters, at most ${s.maxLength}`);
    if (s.enum && !s.enum.includes(v)) errors.push(`${at}: "${v.slice(0, 40)}" is not one of ${s.enum.join(', ')}`);
  } else if (k === 'number') {
    if (!Number.isFinite(v) || (s.type === 'integer' && !Number.isInteger(v))) errors.push(`${at}: expected ${s.type}`);
    if (v < s.minimum || v > s.maximum) errors.push(`${at}: ${v} is outside ${s.minimum} to ${s.maximum}`);
  } else if (k === 'array') {
    if (v.length > s.maxItems) errors.push(`${at}: ${v.length} items, at most ${s.maxItems}`);
    if (v.length < (s.minItems || 0)) errors.push(`${at}: at least ${s.minItems} item`);
    v.slice(0, s.maxItems).forEach((item, i) => check(s.items, item, `${at}[${i}]`, errors));
  } else if (k === 'object') {
    for (const key of s.required || []) if (!(key in v)) errors.push(`${at}.${key}: is required`);
    for (const key of Object.keys(v)) {
      if (!Object.hasOwn(s.properties, key)) errors.push(`${at}.${key}: unknown key`);
      else check(s.properties[key], v[key], `${at}.${key}`, errors);
    }
  }
}

// facts: { projects: Set<name>, envs: Set<name>, nodes: Set<flow node id>, figures: Set<figure id> }
function refs(t, at, facts, errors) {
  if (!t || typeof t !== 'object') return;
  if (t.view === 'overview') {
    if (typeof t.project === 'string' && !facts.projects.has(t.project)) errors.push(`${at}: project "${t.project.slice(0, 40)}" is not in the facts`);
    if (typeof t.env === 'string' && !facts.envs.has(t.env)) errors.push(`${at}: environment "${t.env.slice(0, 40)}" is not in the facts`);
  } else if (t.view === 'flow') {
    if (typeof t.node === 'string' && !facts.nodes.has(t.node)) errors.push(`${at}: flow node "${t.node.slice(0, 40)}" is not in the facts`);
  } else if (t.view === 'timeline') {
    if (typeof t.env === 'string' && !facts.envs.has(t.env)) errors.push(`${at}: environment "${t.env.slice(0, 40)}" is not in the facts`);
    if (typeof t.from === 'string' && typeof t.to === 'string' && t.from > t.to) errors.push(`${at}: the range ends before it starts`);
  } else if (t.view === 'figures') {
    if (typeof t.figure === 'string' && !facts.figures.has(t.figure)) errors.push(`${at}: figure "${t.figure.slice(0, 40)}" is not in the facts`);
  }
}

export function validate(spec, facts) {
  const errors = [];
  if (kind(spec) !== 'object') return { ok: false, errors: ['specification: expected an object'] };
  check(schema, spec, 'spec', errors);
  const each = (key, f) => { if (Array.isArray(spec[key])) spec[key].forEach((x, i) => { if (x && typeof x === 'object') f(x, `spec.${key}[${i}]`); }); };
  each('emphasis', (e, at) => refs(e.target, at + '.target', facts, errors));
  const seen = new Set();
  each('annotations', (a, at) => {
    refs(a.target, at + '.target', facts, errors);
    if (seen.has(a.id)) errors.push(`${at}.id: "${String(a.id).slice(0, 40)}" is used twice`); seen.add(a.id);
    const used = Array.isArray(spec.factsUsed) ? spec.factsUsed.length : 0;
    if (Array.isArray(a.factRefs)) for (const r of a.factRefs) if (Number.isInteger(r) && r >= used) errors.push(`${at}.factRefs: ${r} names no entry of factsUsed`);
  });
  each('goals', (g, at) => { if (Number.isInteger(g.done) && Number.isInteger(g.total) && g.done > g.total) errors.push(`${at}: ${g.done} done of ${g.total}`); });
  if (Array.isArray(spec.figures)) spec.figures.forEach((f, i) => { if (typeof f === 'string' && !facts.figures.has(f) && FIGURES.includes(f)) errors.push(`spec.figures[${i}]: figure "${f}" is not in the facts`); });
  if (Array.isArray(spec.order?.views) && new Set(spec.order.views).size !== spec.order.views.length) errors.push('spec.order.views: a view is listed twice');
  return { ok: errors.length === 0, errors };
}

export function factsOf(ds, flowNodes = ['integrated', 'wait-staging', 'staging', 'held', 'approval', 'production']) {
  return { projects: new Set(ds.projects.map(p => p.name)), envs: new Set(ds.envs), nodes: new Set(flowNodes), figures: new Set(FIGURES) };
}

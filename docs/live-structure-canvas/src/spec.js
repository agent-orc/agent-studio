// View specification for persona assessments (structure-view-spec/v1).
// A model call returns this declarative document; the generic renderer draws
// it. It carries no code, markup or styles. The schema below is the single
// source: build.mjs writes it to schema/ and validate() enforces it, plus the
// checks a JSON Schema cannot express (references must name current facts).
(function (root) {
  'use strict';

  const LAYERS = ['perf', 'storage', 'backup', 'warnings', 'health', 'version', 'drift'];
  const safeText = max => ({ type: 'string', maxLength: max, plainText: true });
  const id = { type: 'string', pattern: '^[A-Za-z0-9._@:-]{1,80}$' };

  const schema = {
    $schema: 'https://json-schema.org/draft/2020-12/schema',
    $id: 'https://agent-studio.local/schemas/structure-view-spec.v1.schema.json',
    title: 'Structure view specification v1',
    description: 'Declarative assessment a persona returns for the live structure canvas. Plain text only; no code, markup or styles.',
    type: 'object', additionalProperties: false,
    required: ['schemaVersion', 'id', 'revision', 'persona', 'createdAt', 'model', 'confidence', 'factsUsed', 'headline'],
    properties: {
      schemaVersion: { const: 'structure-view-spec/v1' },
      id,
      revision: { type: 'integer', minimum: 1 },
      persona: { type: 'object', additionalProperties: false, required: ['id', 'role', 'title'],
        properties: { id, role: { enum: ['operations', 'reliability', 'cost', 'security', 'quality'] }, title: safeText(60) } },
      createdAt: { type: 'string', format: 'date-time' },
      model: { type: 'object', additionalProperties: false, required: ['capabilityClass'],
        properties: { capabilityClass: { enum: ['fast', 'balanced', 'frontier'] }, id: { type: 'string', maxLength: 64, pattern: '^[a-z0-9.-]+$' } } },
      confidence: { type: 'number', minimum: 0, maximum: 1 },
      tokens: { type: 'object', additionalProperties: false, properties: { input: { type: 'integer', minimum: 0 }, output: { type: 'integer', minimum: 0 } } },
      trigger: safeText(120),
      factsUsed: { type: 'array', minItems: 1, maxItems: 16, items: { type: 'string', maxLength: 120, pattern: '^[A-Za-z0-9._@:/-]+$' } },
      headline: safeText(160),
      judgement: safeText(700),
      layers: { type: 'object', additionalProperties: false, properties: {
        enable: { type: 'array', maxItems: 7, items: { enum: LAYERS } }, emphasise: { enum: LAYERS } } },
      emphasis: { type: 'array', maxItems: 12, items: { type: 'object', additionalProperties: false, required: ['node'],
        properties: { node: id, weight: { enum: ['strong', 'normal'] } } } },
      annotations: { type: 'array', maxItems: 6, items: { type: 'object', additionalProperties: false, required: ['id', 'tone', 'title'],
        properties: { id, node: id, link: id, tone: { enum: ['bad', 'warn', 'info', 'good'] }, title: safeText(48), text: safeText(160),
          factRefs: { type: 'array', maxItems: 6, items: id } } } },
      ranking: { type: 'array', maxItems: 40, items: id },
      goals: { type: 'array', maxItems: 3, items: { type: 'object', additionalProperties: false, required: ['id', 'title', 'done', 'total'],
        properties: { id, title: safeText(90), done: { type: 'integer', minimum: 0 }, total: { type: 'integer', minimum: 1 },
          steps: { type: 'array', maxItems: 6, items: { type: 'object', additionalProperties: false, required: ['kind', 'title', 'state'],
            properties: { kind: { enum: ['owner-decision', 'card', 'operation'] }, title: safeText(90), detail: safeText(200),
              state: { enum: ['open', 'proposed', 'in-progress', 'done'] } } } } } } },
    },
  };

  // Plain text: no markup, no control characters, no URL schemes that execute.
  const UNSAFE = /[<>\u0000-\u0008\u000b\u000c\u000e-\u001f]|javascript:|data:/i;
  const DATE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;

  function check(s, v, path, errors) {
    if (s.const !== undefined && v !== s.const) errors.push(path + ' must be ' + s.const);
    if (s.enum && !s.enum.includes(v)) errors.push(path + ' is not one of ' + s.enum.join(', '));
    const t = s.type;
    if (t === 'object') {
      if (!v || typeof v !== 'object' || Array.isArray(v)) { errors.push(path + ' must be an object'); return; }
      for (const r of s.required || []) if (!(r in v)) errors.push(path + '.' + r + ' is required');
      for (const k of Object.keys(v)) {
        if (!s.properties || !(k in s.properties)) { if (s.additionalProperties === false) errors.push(path + '.' + k + ' is not allowed'); continue; }
        check(s.properties[k], v[k], path + '.' + k, errors);
      }
    } else if (t === 'array') {
      if (!Array.isArray(v)) { errors.push(path + ' must be an array'); return; }
      if (s.minItems && v.length < s.minItems) errors.push(path + ' needs at least ' + s.minItems + ' items');
      if (s.maxItems && v.length > s.maxItems) errors.push(path + ' allows at most ' + s.maxItems + ' items');
      v.forEach((x, i) => check(s.items, x, path + '[' + i + ']', errors));
    } else if (t === 'string') {
      if (typeof v !== 'string') { errors.push(path + ' must be a string'); return; }
      if (s.maxLength && v.length > s.maxLength) errors.push(path + ' is longer than ' + s.maxLength);
      if (s.pattern && !new RegExp(s.pattern).test(v)) errors.push(path + ' has an invalid format');
      if (s.format === 'date-time' && !DATE.test(v)) errors.push(path + ' must be a date-time');
      if (s.plainText && UNSAFE.test(v)) errors.push(path + ' must be plain text');
    } else if (t === 'integer' || t === 'number') {
      if (typeof v !== 'number' || !isFinite(v) || (t === 'integer' && !Number.isInteger(v))) { errors.push(path + ' must be a ' + t); return; }
      if (s.minimum !== undefined && v < s.minimum) errors.push(path + ' is below ' + s.minimum);
      if (s.maximum !== undefined && v > s.maximum) errors.push(path + ' is above ' + s.maximum);
    }
  }

  // facts: { nodes: Set, links: Set, warnings: Set }. References must name current facts.
  function validate(spec, facts) {
    const errors = [];
    check(schema, spec, 'spec', errors);
    const list = v => (Array.isArray(v) ? v.filter(x => x && typeof x === 'object') : []);
    if (facts && spec && typeof spec === 'object') {
      for (const e of list(spec.emphasis)) if (!facts.nodes.has(e.node)) errors.push('emphasis names unknown node ' + e.node);
      for (const a of list(spec.annotations)) {
        if (!a.node === !a.link) errors.push('annotation ' + a.id + ' needs exactly one of node or link');
        if (a.node && !facts.nodes.has(a.node)) errors.push('annotation ' + a.id + ' names unknown node ' + a.node);
        if (a.link && !facts.links.has(a.link)) errors.push('annotation ' + a.id + ' names unknown link ' + a.link);
      }
      for (const g of list(spec.goals)) if (g.done > g.total) errors.push('goal ' + g.id + ' reports more done than total');
    }
    return { ok: errors.length === 0, errors };
  }

  const api = { schema, validate, LAYERS };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.LSCSpec = api;
})(typeof window !== 'undefined' ? window : globalThis);

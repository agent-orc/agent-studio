// The board-oriented operator commands share the existing client and write fence.
import fs from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { cardWrite, readCard, watchPathFor } from './commands.mjs';
import { usage, positiveInteger } from './args.mjs';
import { OperationError } from './studio.mjs';
import { acquireLocalLock } from './watch.mjs';
import { isTokenShaped } from './credential.mjs';

const enc = encodeURIComponent;
const lanes = {
  backlog: '0-backlog', preparation: '1-preparation', orchestratorPrep: '1a-orchestrator-prep',
  ready: '2-ready', progress: '3-progress', failedPickup: '3a-failed-pickup',
  codeNotComplete: '3b-code-not-complete', autoReview: '4-auto-review',
  humanReview: '5-human-review', escalated: '5e-escalated', completed: '6-completed', archive: '7-archive',
};
const keyOf = card => card.key ?? card.taskKey ?? card.id;
const firstLine = value => String(value ?? '').split(/\r?\n/, 1)[0];
const textOf = value => typeof value === 'object' && value !== null ? JSON.stringify(value) : String(value ?? '-');
const sameKey = (left, right) => String(left).toLowerCase() === String(right).toLowerCase();
function needKeys(keys) {
  if (!keys.length || keys.some(key => !key)) throw usage('missing-argument', 'Name at least one card key.');
  return keys;
}
function laneOf(value) {
  if (['done', 'accepted', 'completed'].includes(value?.toLowerCase())) return lanes.completed;
  if (['archived'].includes(value?.toLowerCase())) return lanes.archive;
  const match = Object.entries(lanes).find(([name, state]) => sameKey(name, value) || sameKey(state, value));
  if (!match) throw usage('invalid-lane', `Unknown lane ${value}. Use a lane name or its full state.`);
  return match[1];
}
async function board(ctx) {
  const grouped = await ctx.client.request('GET', '/api/tasks/grouped');
  return Object.entries(grouped).flatMap(([lane, cards]) => lane !== 'review' && Array.isArray(cards)
    ? cards.map(card => ({ ...card, state: card.state ?? lanes[lane] ?? lane })) : []);
}
function summary(card) {
  return { key: keyOf(card), lane: card.state, cli: card.cliType ?? card.agent ?? null,
    model: card.model ?? null, level: card.thinkingLevel ?? null,
    parkedReason: card.parkedBlocker?.reason ?? null,
    reviewVerdict: card.orchestratorVerdict ?? card.reviewProjection?.latestOutcome ?? null,
    integration: card.integration ?? null };
}
function cardLine(card) {
  return `${card.key} ${card.lane} cli=${card.cli ?? '-'} model=${card.model ?? '-'} level=${card.level ?? '-'} parked=${firstLine(card.parkedReason) || '-'} review=${card.reviewVerdict ?? '-'} integration=${card.integration?.status ?? '-'}`;
}
function result(data, text) { return { data, text }; }
function command(summary, run, spec = {}) { return { summary, run, spec, taskProtocol: true }; }

async function batch(keys, act) {
  needKeys(keys);
  const items = [];
  // Card mutations are sequential. A failure leaves prior successes visible and stops the batch.
  for (const key of keys) {
    try { items.push({ key, answer: await act(key) }); }
    catch (error) {
      if (error instanceof OperationError) error.body = { ...(error.body ?? {}), completed: items, failedCard: key };
      throw error;
    }
  }
  return result({ items }, items.map(item => `${item.key} done`).join('\n'));
}
async function dependencies(ctx, key, dependsOn, project) {
  const card = await readCard(ctx, key, project);
  // /references replaces all relations. Preserve unrelated edges and fence the read too.
  const references = card.references ?? {};
  return cardWrite(ctx, key, project ?? card.projectName, 'PUT', '/references', {
    ...references, dependsOn, expectedReferences: references,
  });
}
async function top(ctx, keys, project) {
  needKeys(keys);
  // Head insertion reverses order, so promote from last to first. Each move is fenced.
  const answer = await batch([...keys].reverse(), key => cardWrite(ctx, key, project, 'POST', '/move',
    { targetState: '2-ready', targetIndex: 0 }));
  answer.data.items.reverse();
  answer.text = keys.map(key => `${key} first in Ready`).join('\n');
  return answer;
}
async function snapshot(ctx) {
  const [cards, triage, paths, archive] = await Promise.all([
    board(ctx), ctx.client.request('GET', '/api/operator/batch-triage'), ctx.client.request('GET', '/api/watch-paths'),
    ctx.client.request('GET', '/api/tasks/archive', { query: { limit: 1 } }),
  ]);
  const gates = [];
  for (const project of paths) {
    const state = await ctx.client.request('GET', `/api/projects/${enc(project.name)}/batch-gate/state`);
    gates.push({ project: project.name, slots: state.gateSlots ?? null, pending: state.pending ?? [] });
  }
  const counts = Object.fromEntries(Object.values(lanes).map(lane => [lane, cards.filter(card => card.state === lane).length]));
  counts[lanes.archive] = archive.total;
  const progress = cards.filter(card => card.state === lanes.progress).map(summary);
  const escalated = [];
  // Board park summaries truncate reasons; use the full detail contract.
  for (const item of cards.filter(card => card.state === lanes.escalated)) {
    const card = await readCard(ctx, keyOf(item), item.projectName);
    escalated.push({ key: keyOf(card), date: card.parkedBlocker?.parkedAt ?? card.enteredLaneAt ?? null,
      reason: firstLine(card.parkedBlocker?.reason ?? card.reviewProjection?.decisionRequired?.reason) });
  }
  const acceptedReview = cards.filter(card => card.state === lanes.humanReview && card.orchestratorVerdict === 'accept').map(summary);
  return { counts, progress, escalated, acceptedReview, gates,
    batches: (triage.projects ?? []).flatMap(project => (project.flights ?? []).map(flight => ({ project: project.project, ...flight }))) };
}

function jsonFile(file) {
  let value;
  try { value = fs.readFileSync(file, 'utf8'); }
  catch (error) { throw usage('file-unreadable', `Cannot read ${file} (${error.code}).`); }
  if (isTokenShaped(value)) throw usage('token-in-content', 'The input file contains a token-shaped value.');
  try { return JSON.parse(value); }
  catch { throw usage('invalid-json', `${file} must contain valid JSON.`); }
}
function validateCards(input) {
  const cards = Array.isArray(input) ? input : input?.cards;
  if (!Array.isArray(cards) || !cards.length) throw usage('invalid-cards', 'The file must contain a nonempty array of cards, or {"cards": [...]} .');
  const byId = new Map();
  for (const [index, card] of cards.entries()) {
    if (!card || typeof card !== 'object' || !['title', 'project'].every(field => typeof card[field] === 'string' && card[field].trim()))
      throw usage('invalid-card', `Card ${index + 1} needs a title and project.`);
    card.localId = card.id ?? String(index + 1);
    if (typeof card.localId !== 'string' || byId.has(card.localId)) throw usage('invalid-local-id', 'Card local ids must be unique strings.');
    if (card.dependsOn !== undefined && (!Array.isArray(card.dependsOn) || card.dependsOn.some(key => typeof key !== 'string')))
      throw usage('invalid-dependencies', `${card.localId}: dependsOn must be an array of local ids.`);
    byId.set(card.localId, card);
  }
  const visiting = new Set();
  const visited = new Set();
  function visit(card) {
    if (visiting.has(card.localId)) throw usage('dependency-cycle', 'The input cards contain a dependency cycle.');
    if (visited.has(card.localId)) return;
    visiting.add(card.localId);
    for (const id of card.dependsOn ?? []) {
      if (!byId.has(id)) throw usage('unknown-local-id', `${card.localId} depends on unknown local id ${id}.`);
      visit(byId.get(id));
    }
    visiting.delete(card.localId); visited.add(card.localId);
  }
  cards.forEach(visit);
  return cards;
}
async function createCards(ctx, file) {
  const cards = validateCards(jsonFile(file));
  const projects = new Map();
  // Resolve every project before the first mutation.
  for (const card of cards) if (!projects.has(card.project)) projects.set(card.project, await watchPathFor(ctx, card.project));
  const created = [];
  try {
    for (const card of cards) {
      const answer = await ctx.client.request('POST', '/api/tasks/', { body: {
        title: card.title, watchPath: projects.get(card.project).path, targetState: '0-backlog',
        mode: card.mode, taskType: card.type ?? 'chore', thinkingLevel: card.level,
        cliType: card.cli, agent: card.cli, model: card.model, promptMarkdown: card.prompt,
        creationSource: 'operator-agent', createdBy: 'studio-op',
      } });
      const key = answer.taskKey ?? answer.key;
      if (!key) throw new OperationError('created-key-missing', 'The create response has no stable task key; created cards remain in Backlog.', { body: { answer } });
      created.push({ localId: card.localId, key, project: projects.get(card.project).name });
    }
    const keys = new Map(created.map(item => [item.localId, item.key]));
    // Every card exists before references are set. No card reaches Ready before all edges exist.
    for (const card of cards) await dependencies(ctx, keys.get(card.localId), (card.dependsOn ?? []).map(id => keys.get(id)), projects.get(card.project).name);
    for (const project of projects.keys()) await top(ctx, created.filter(card => card.project === projects.get(project).name).map(card => card.key), projects.get(project).name);
  } catch (error) {
    if (error instanceof OperationError) error.body = { ...(error.body ?? {}), created,
      recovery: 'Inspect the created keys before retrying; create is not transactional and does not roll back.' };
    throw error;
  }
  return result({ cards: created }, created.map(card => `${card.localId} -> ${card.key} Ready`).join('\n'));
}

async function why(ctx, key, project) {
  const card = await readCard(ctx, key, project);
  if (card.state !== lanes.ready) return result({ key, lane: card.state, reasons: [] }, `${key} is in ${card.state}; why applies to Ready cards.`);
  const capacity = await ctx.client.request('GET', '/api/operator/coding-capacity');
  const pool = (capacity.projects ?? []).find(pool => sameKey(pool.project, project ?? card.projectName));
  const entry = (pool?.cards ?? capacity.fleet?.cards ?? []).find(item => sameKey(item.cardKey, keyOf(card)));
  const reasons = [...(entry?.blocks ?? [])];
  if (pool && pool.freeSlots === 0) reasons.push({ class: 'slot', reason: `${pool.usedSlots}/${pool.totalSlots} coding slots used in ${pool.project}.` });
  if (!entry) reasons.push({ class: 'unknown', reason: 'The card is absent from the published coding-capacity snapshot; read it again after the board refresh.' });
  if (!reasons.length) reasons.push({ class: 'unexplained', reason: 'No published blocker; claimable capacity is a snapshot, not a dispatch permit. Check runner mode and the next pickup tick.' });
  return result({ key, lane: card.state, reasons, slots: pool ? { total: pool.totalSlots, used: pool.usedSlots, free: pool.freeSlots } : null },
    reasons.map(reason => `${key} ${reason.class}: ${reason.reason}`).join('\n'));
}

async function raw(ctx, method, args) {
  const [route, json] = args;
  if (!route || (method !== 'GET' && json === undefined) || args.length > (method === 'GET' ? 1 : 2))
    throw usage('missing-argument', `${method.toLowerCase()} PATH${method === 'GET' ? '' : ' JSON'}`);
  let body;
  if (json !== undefined) {
    if (isTokenShaped(json)) throw usage('token-in-content', 'The JSON contains a token-shaped value.');
    try { body = JSON.parse(json); } catch { throw usage('invalid-json', 'The request body must be valid JSON.'); }
  }
  // Validate the origin before acquiring attention. Preserve the original query on both calls.
  const target = ctx.client.url(route);
  const match = target.pathname.match(/\/api\/tasks\/([^/]+)(\/.*)?$/);
  let answer;
  if (method !== 'GET' && match && !['batch', 'batch-move', 'reorder', 'reference-status'].includes(match[1]) && match[2] !== '/attention') {
    const attention = await ctx.client.request('POST', `/api/tasks/${match[1]}/attention${target.search}`);
    answer = await ctx.client.request(method, route, { body, headers: { 'If-Match': attention.revision } });
  } else answer = await ctx.client.request(method, route, { body });
  return result({ answer }, textOf(answer));
}

// Snapshot polling covers ordinary lane changes, which the decision stream omits.
export async function watchBoard(ctx, options) {
  ctx.output.jsonLines = true;
  const interval = positiveInteger(options.interval ?? '2', 'interval', { max: 3600 });
  const timeout = options.timeout === undefined ? null : positiveInteger(options.timeout, 'timeout');
  const controller = new AbortController();
  const stop = () => controller.abort();
  process.once('SIGINT', stop); process.once('SIGTERM', stop);
  const timer = timeout === null ? null : setTimeout(stop, timeout * 1000);
  let lock = null;
  let lease = null;
  let heartbeatError = null;
  let heartbeat = null;
  let heartbeatRequest = Promise.resolve();
  let previousCards = null;
  let previousRed = null;
  function emit(event) {
    const line = ctx.output.json ? JSON.stringify(event)
      : `${event.time} ${event.type} ${event.key ?? event.batchId} ${event.from ? `${event.from} -> ` : ''}${event.to ?? event.reason ?? ''}`;
    ctx.output.stdout.write(ctx.output.clean(`${line}\n`));
  }
  try {
    lock = acquireLocalLock(ctx.configDir, { origin: ctx.client.base.origin, session: 'board-watch' });
    const granted = await ctx.client.request('POST', '/api/operator/watcher/lease', { body: { session: 'board-watch', takeOver: false } });
    lease = granted.lease;
    lock.update({ leaseId: lease.leaseId });
    const renew = () => {
      heartbeat = setTimeout(() => {
        heartbeatRequest = ctx.client.request('POST', `/api/operator/watcher/lease/${enc(lease.leaseId)}/heartbeat`)
          .then(() => { if (!controller.signal.aborted) renew(); })
          .catch(error => { heartbeatError = error; stop(); });
      }, lease.heartbeatSeconds * 1000);
    };
    renew();
    ctx.output.note('Watching lane, escalation and gate changes.');
    while (!controller.signal.aborted) {
      const [cards, triage] = await Promise.all([board(ctx), ctx.client.request('GET', '/api/operator/batch-triage')]);
      const current = new Map(cards.map(card => [`${card.projectName}:${keyOf(card)}`, card]));
      const now = new Date().toISOString();
      if (previousCards) for (const [id, card] of current) {
        const previous = previousCards.get(id);
        if (previous?.state !== card.state) emit({ time: now, type: 'lane.changed', key: keyOf(card), project: card.projectName, from: previous?.state ?? null, to: card.state });
        const marker = card.parkedBlocker?.parkedAt ?? card.enteredLaneAt;
        const priorMarker = previous?.parkedBlocker?.parkedAt ?? previous?.enteredLaneAt;
        if (card.state === lanes.escalated && (previous?.state !== card.state || marker !== priorMarker))
          emit({ time: now, type: 'escalation.opened', key: keyOf(card), project: card.projectName, reason: firstLine(card.parkedBlocker?.reason) });
      }
      if (previousCards) for (const [id, previous] of previousCards) {
        if (current.has(id)) continue;
        try {
          const card = await readCard(ctx, keyOf(previous), previous.projectName);
          if (card.state !== previous.state) emit({ time: now, type: 'lane.changed', key: keyOf(card), project: card.projectName, from: previous.state, to: card.state });
        } catch (error) { if (error.status !== 404) throw error; }
      }
      const red = new Map();
      for (const project of triage.projects ?? []) for (const flight of project.flights ?? []) {
        if (flight.phase !== 'red' && !flight.isolating) continue;
        const id = `${project.project}:${flight.batchId}`;
        red.set(id, flight);
        if (previousRed && !previousRed.has(id)) emit({ time: now, type: 'gate.red', project: project.project, batchId: flight.batchId, reason: flight.phaseReason ?? flight.phase });
      }
      for (const project of triage.projects ?? []) for (const member of project.members ?? []) {
        if (!member.red) continue;
        const id = `${project.project}:card:${member.taskKey}`;
        red.set(id, member);
        if (previousRed && !previousRed.has(id)) emit({ time: now, type: 'gate.red', project: project.project, key: member.taskKey, reason: member.nextOperation?.reason ?? member.status });
      }
      previousCards = current; previousRed = red;
      if (!controller.signal.aborted) await delay(interval * 1000, null, { signal: controller.signal }).catch(error => { if (error.name !== 'AbortError') throw error; });
    }
    if (heartbeatError) throw heartbeatError;
    return { streamed: true };
  } finally {
    stop(); clearTimeout(timer); clearTimeout(heartbeat);
    await heartbeatRequest;
    process.removeListener('SIGINT', stop); process.removeListener('SIGTERM', stop);
    if (lease) {
      try { await ctx.client.request('DELETE', `/api/operator/watcher/lease/${enc(lease.leaseId)}`); }
      catch (error) { ctx.output.note(`Watcher lease release failed: ${error.message}`); }
    }
    lock?.release();
  }
}

export function workingSetCommands(existing) {
  return {
    snap: command('Lane counts, Progress, escalations, accept verdicts, gates and open batches.', async ctx => {
      const data = await snapshot(ctx);
      return result(data, [Object.entries(data.counts).map(([lane, count]) => `${lane}=${count}`).join(' '),
        ...data.progress.map(card => `running ${card.key} ${card.cli ?? '-'}`),
        ...data.escalated.map(card => `escalated ${card.key} ${card.date ?? '-'} ${card.reason || '-'}`),
        ...data.acceptedReview.map(card => `accept ${card.key}`),
        ...data.gates.map(gate => `gate ${gate.project} slots=${textOf(gate.slots)} pending=${gate.pending.length}`),
        ...data.batches.map(flight => `batch ${flight.project} ${flight.batchId} ${flight.phase}`)].join('\n'));
    }),
    card: command('card KEY...: lane, route, parked reason, review and integration.', async (ctx, keys, options) => {
      const cards = [];
      for (const key of needKeys(keys)) cards.push(summary(await readCard(ctx, key, options.project)));
      return result({ cards }, cards.map(cardLine).join('\n'));
    }, { project: 'string' }),
    ready: command('Ready cards in server queue order, with model and level.', async ctx => {
      const cards = (await board(ctx)).filter(card => card.state === lanes.ready).map(summary);
      return result({ cards }, cards.map(card => `${card.key} ${card.model ?? '-'} ${card.level ?? '-'}`).join('\n') || 'Ready is empty.');
    }),
    deps: command('deps [LANE...]: open dependency edges and predecessor lanes.', async (ctx, args) => {
      const selected = args.map(laneOf);
      const cards = (await board(ctx)).filter(card => ![lanes.completed, lanes.archive].includes(card.state) && (!selected.length || selected.includes(card.state)));
      const edges = [];
      for (const item of cards) {
        const card = await readCard(ctx, keyOf(item), item.projectName);
        for (const dependency of card.references?.dependsOn ?? []) {
          const key = typeof dependency === 'string' ? dependency : dependency.key;
          let predecessor;
          try { predecessor = await readCard(ctx, key); }
          catch (error) { if (error.status !== 404) throw error; }
          edges.push({ key: keyOf(card), lane: card.state, predecessor: key, predecessorLane: predecessor?.state ?? 'not-found' });
        }
      }
      return result({ edges }, edges.map(edge => `${edge.key} -> ${edge.predecessor} ${edge.predecessorLane}`).join('\n') || 'No dependency edges.');
    }),
    continue: { ...existing.continue, taskProtocol: true,
      async run(ctx, args, options) {
        const [key, file, level, cli, model] = args;
        needKeys([key]);
        if (args.length > 5) throw usage('unexpected-argument', 'continue KEY FILE [LEVEL] [CLI] [MODEL]');
        return existing.continue.run(ctx, [key], { ...options, promptFile: options.promptFile ?? file,
          thinking: options.thinking ?? level, cli: options.cli ?? cli, model: options.model ?? model });
      } },
    move: { ...existing.move, taskProtocol: true,
      async run(ctx, args, options) {
        // Keep the existing KEY LANE spelling too.
        const laneFirst = Object.entries(lanes).some(([name, lane]) => sameKey(args[0], name) || sameKey(args[0], lane));
        const lane = laneOf(laneFirst ? args[0] : args[1]);
        const keys = laneFirst ? args.slice(1) : [args[0]];
        if (!laneFirst && args.length !== 2) throw usage('unexpected-argument', 'move LANE KEY...');
        return batch(keys, key => existing.move.run(ctx, [key, lane], options).then(answer => answer.data));
      } },
    top: command('top KEY...: put cards first in Ready in the given order.', (ctx, keys, options) => top(ctx, keys, options.project), { project: 'string' }),
    route: command('route CLI MODEL LEVEL KEY...: set card routing.', async (ctx, args, options) => {
      const [cliType, model, thinkingLevel, ...keys] = args;
      needKeys(keys);
      return batch(keys, async key => {
        const answers = [];
        for (const [route, body] of [['cli-type', { cliType }], ['model', { model }], ['thinking-level', { thinkingLevel }]])
          answers.push(await cardWrite(ctx, key, options.project, 'PUT', `/${route}`, body));
        return answers;
      });
    }, { project: 'string' }),
    depends: command('depends KEY DEP...: replace dependencies and preserve other relations.', async (ctx, [key, ...deps], options) => {
      needKeys([key]);
      const answer = await dependencies(ctx, key, deps, options.project);
      return result({ key, dependsOn: deps, answer }, `${key} depends on ${deps.join(', ') || '(none)'}`);
    }, { project: 'string' }),
    accept: { ...existing.accept, taskProtocol: true,
      run: (ctx, keys, options) => keys.length === 1 ? existing.accept.run(ctx, keys, options)
        : batch(keys, key => existing.accept.run(ctx, [key], options).then(answer => answer.data)) },
    'accept-with-reason': command('accept-with-reason KEY "reason": request server-authorized completion override.', async (ctx, args, options) => {
      const [key, reason] = args;
      if (args.length !== 2 || !reason.trim()) throw usage('missing-argument', 'accept-with-reason KEY "reason"');
      if (isTokenShaped(reason)) throw usage('token-in-content', 'The reason contains a token-shaped value.');
      const answer = await cardWrite(ctx, key, options.project, 'POST', '/move', { targetState: '6-completed', operatorOverride: true, reason });
      return result({ key, answer }, `Accepted ${key}: ${reason}`);
    }, { project: 'string' }),
    create: { ...existing.create, taskProtocol: true,
      run(ctx, args, options) {
        if (!args.length) return existing.create.run(ctx, args, options);
        if (args.length !== 1) throw usage('unexpected-argument', 'create FILE');
        return createCards(ctx, args[0]);
      } },
    quota: command('Subscription usage percent and reset times.', async ctx => {
      const { quota = [] } = await ctx.client.request('GET', '/api/operator/capacity');
      const subscriptions = quota.flatMap(item => {
        const snapshot = item.snapshot ?? {};
        const windows = snapshot.windows?.length ? snapshot.windows : [{ label: 'unknown' }];
        return windows.map(window => ({ cliType: item.cliType, subscriptionKey: snapshot.subscriptionKey ?? null,
          window: window.label, usedPercent: window.usedPct ?? null, resetAt: window.resetAt ?? null,
          status: snapshot.error ?? (snapshot.suspicious ? 'suspicious' : item.fresh ? 'fresh' : 'cached') }));
      });
      return result({ subscriptions }, subscriptions.map(item => `${item.cliType} ${item.subscriptionKey ?? '-'} ${item.window} ${item.usedPercent ?? '?'}% reset=${item.resetAt ?? 'unknown'} ${item.status}`).join('\n') || 'No subscription readings.');
    }),
    why: command('why KEY: published dependency, dispatch, quota and slot blockers.', (ctx, args, options) => {
      if (args.length !== 1) throw usage('missing-argument', 'why KEY');
      return why(ctx, args[0], options.project);
    }, { project: 'string' }),
    watch: { ...existing.watch, taskProtocol: true,
      summary: 'Print lane, escalation and red gate changes; --decisions keeps the decision watcher.',
      spec: { ...existing.watch.spec, interval: 'string', decisions: 'boolean' },
      run(ctx, args, options) {
        if (args.length) throw usage('unexpected-argument', 'watch takes options only.');
        if (options.decisions || options.session || options.fromStart || options.after || options.takeOver)
          return existing.watch.run(ctx, args, options);
        return watchBoard(ctx, options);
      } },
    ...Object.fromEntries(['get', 'post', 'put'].map(verb => [verb,
      command(`${verb} PATH${verb === 'get' ? '' : ' JSON'}: call the API, fencing card writes.`, (ctx, args) => raw(ctx, verb.toUpperCase(), args))])),
  };
}

import fs from 'node:fs';
import { evaluateAcceptance } from './accept-policy.mjs';
import { positiveInteger, required, usage } from './args.mjs';
import { describeToken, inspectCredential, isTokenShaped } from './credential.mjs';
import { EXIT } from './exit-codes.mjs';
import { OperationError, failure } from './studio.mjs';
import { watchDecisions } from './watch.mjs';

const enc = encodeURIComponent;
const MAX_TEXT_BYTES = 256 * 1024;
const LOG_KINDS = ['observed', 'decided', 'did', 'handoff'];
const CONTINUE_MODES = ['continue', 'steer', 'extend'];
const CLI_TYPES = ['claude', 'codex', 'copilot', 'gemini'];
const PREFLIGHTS = ['restart', 'deploy'];
// Acceptance is its own operation (OA-D5), and archiving stays with the Owner.
const ACCEPTED_LANES = ['6-completed', '7-archive', '5-completed', 'completed', 'done', 'accepted', 'rejected', 'archived'];

/** Routes an operator credential never calls; Studio refuses them (security overview). */
const REFUSED_ROUTES = [
  '/api/auth/* (access, tokens, sessions)',
  '/api/v1/* (Task Server plane)',
  'Runner service routes',
  'SignalR hubs (/hubs/*)',
  'installation-wide settings writes',
];

/** What stays with the Owner (Dossier "What stays with the owner", security model). */
const OWNER_KEEPS = [
  'credentials: CLI logins, provider keys, GitHub App, SSH keys, secret values',
  'consent clicks: device-code and OAuth approvals, provider terms',
  'installation-wide settings: access, roles, tokens, grants, operation catalogue, retention',
  'the second factor: step-up, T2 approvals, break-glass, kill switch',
  'product decisions: decision cards, Dossier acceptance, cards whose evidence is not green',
];

function readText(file, flag) {
  let text;
  try {
    if (fs.statSync(file).size > MAX_TEXT_BYTES) throw usage('text-file-too-large', `--${flag} is larger than 256 KB.`);
    text = fs.readFileSync(file, 'utf8');
  } catch (error) {
    if (error instanceof OperationError) throw error;
    throw usage('text-file-unreadable', `Cannot read --${flag} ${file} (${error.code}).`);
  }
  return checkedText(text, flag);
}

/** Text studio-op sends is stored by Studio; a token-shaped value never leaves this machine. */
function checkedText(text, flag) {
  if (isTokenShaped(text))
    throw usage('token-in-content', `--${flag} contains a token-shaped value. Remove it; if it is a real token, ask the Owner to revoke it.`);
  return text;
}

function cardPath(card, rest = '') {
  return `/api/tasks/${enc(card)}${rest}`;
}

/**
 * Guardrail "Not collide with the chat" (OA-D3 C): every card write takes or
 * renews the card's attention lease and sends the revision it returned, so a
 * stale write is refused with 412 and an Owner action always wins.
 */
async function cardWrite(ctx, card, project, method, rest, body) {
  const query = { project };
  const attention = await ctx.client.request('POST', cardPath(card, '/attention'), { query });
  return ctx.client.request(method, cardPath(card, rest), { query, body, headers: { 'If-Match': attention.revision } });
}

const WARMING_RETRIES = 20;
const WARMING_DELAY_MS = 500;

/**
 * Reads a card. Right after a create or a restart the route may answer 202
 * `task-index-warming` without the card; that answer is never judged, the
 * read is repeated for a bounded time instead.
 */
async function readCard(ctx, card, project) {
  for (let attempt = 0; ; attempt++) {
    const answer = await ctx.client.send('GET', cardPath(card), { query: { project } });
    if (answer.status === 202 && answer.body?.reason === 'task-index-warming') {
      if (attempt >= WARMING_RETRIES)
        throw new OperationError('task-index-warming', `Studio is still indexing cards; ${card} could not be read yet.`,
          { exitCode: EXIT.CONFLICT });
      await new Promise(resolve => setTimeout(resolve, WARMING_DELAY_MS));
      continue;
    }
    // The detail route wraps the card as `info` beside its prompt and history.
    if (answer.status >= 200 && answer.status < 300) return answer.body?.info ?? answer.body;
    throw failure('GET', cardPath(card), answer);
  }
}

async function watchPathFor(ctx, project) {
  const paths = await ctx.client.request('GET', '/api/watch-paths');
  const match = (Array.isArray(paths) ? paths : []).find(entry =>
    String(entry.name).toLowerCase() === String(project).toLowerCase());
  if (!match) throw new OperationError('unknown-project', `No readable project is named "${project}".`, { exitCode: EXIT.NOT_FOUND });
  return match;
}

async function readableProjects(ctx) {
  const paths = await ctx.client.request('GET', '/api/watch-paths');
  return (Array.isArray(paths) ? paths : []).map(entry => entry.name).filter(Boolean);
}

function card1(positionals, what = 'card') {
  const [card] = positionals;
  if (!card) throw usage('missing-argument', `Name the ${what} key, for example DEMO-12.`);
  return card;
}

function lines(...parts) {
  return parts.flat().filter(part => part !== null && part !== undefined && part !== '').join('\n');
}

export const COMMANDS = {
  'auth status': {
    summary: 'Show the credential file, the identity check and what stays with the Owner.',
    spec: {},
    local: true,
    async run(ctx) {
      const info = inspectCredential(ctx.home, ctx.config);
      const credential = { file: info.file, name: info.name, kind: info.kind, exists: info.exists,
        mode: info.mode ?? null, ownerOnly: info.ownerOnly ?? null,
        ...(ctx.credential ? describeToken(ctx.credential.token) : { prefix: null, id: null }) };
      const server = { url: ctx.url, reachable: false, authenticated: false, status: null, projects: [] };
      let exitCode = EXIT.OK;
      if (ctx.clientError) {
        server.error = ctx.clientError.code;
        exitCode = ctx.clientError.exitCode ?? EXIT.CREDENTIAL;
      } else {
        const probe = await ctx.client.send('GET', '/api/operator/decisions', { query: { limit: 1 } });
        server.reachable = true;
        server.status = probe.status;
        server.authenticated = probe.status >= 200 && probe.status < 300;
        if (server.authenticated) {
          server.actor = ctx.credential ? null : 'local profile (no credential)';
          try { server.projects = await readableProjects(ctx); }
          catch (error) { server.projectsError = error.code; }
        } else {
          exitCode = probe.status === 401 ? EXIT.CREDENTIAL : probe.status === 403 ? EXIT.FORBIDDEN : EXIT.FAILURE;
          server.error = probe.body?.error ?? `http-${probe.status}`;
        }
      }
      const data = { credential, server, refusedRoutes: REFUSED_ROUTES, ownerKeeps: OWNER_KEEPS,
        securityModel: 'docs/security-model/index.html' };
      return { exitCode, ok: exitCode === EXIT.OK, data, text: lines(
        `Studio:      ${ctx.url}`,
        `Credential:  ${credential.file} (${credential.exists ? `mode ${credential.mode}${credential.ownerOnly ? ', owner-only' : ', NOT owner-only'}` : 'missing'})`,
        credential.prefix ? `Token:       ${credential.kind}, prefix ${credential.prefix}${credential.id ? `, id ${credential.id}` : ''}` : null,
        `Server:      ${server.authenticated ? 'authenticated' : `not authenticated (${server.error ?? server.status})`}`,
        server.projects.length ? `Projects:    ${server.projects.join(', ')}` : null,
        'Refused to operator credentials:', REFUSED_ROUTES.map(item => `  - ${item}`),
        'Stays with the Owner:', OWNER_KEEPS.map(item => `  - ${item}`),
        'Security model: docs/security-model/index.html') };
    },
  },

  watch: {
    summary: 'Take the watcher lease, block until the first actionable decision event, exit with its class code.',
    spec: { session: 'string', takeOver: 'boolean', reason: 'string', after: 'string', fromStart: 'boolean', timeout: 'string' },
    async run(ctx, _positionals, options) {
      if (options.takeOver && !options.reason)
        throw usage('missing-option', '--take-over needs --reason; the take-over is audited.');
      if (options.after !== undefined && !/^\d+$/.test(options.after))
        throw usage('invalid-option', '--after must be a cursor from the decision stream.');
      const result = await watchDecisions({
        client: ctx.client, output: ctx.output, directory: ctx.configDir, session: options.session,
        takeOver: options.takeOver, reason: options.reason, after: options.after, fromStart: options.fromStart,
        timeoutSeconds: options.timeout ? positiveInteger(options.timeout, 'timeout', { max: 86_400 }) : null,
      });
      const event = result.event;
      return { exitCode: result.exitCode, data: { event: event ?? null, cursor: result.cursor ?? null,
        reason: result.reason, exitCode: result.exitCode },
        text: event ? lines(
          `${event.type}${event.project ? ` ${event.project}` : ''}${event.cardKey ? ` ${event.cardKey}` : ''}: ${event.reason ?? ''}`,
          event.suggestedOperations?.length ? `Suggested: ${event.suggestedOperations.join(', ')}` : null,
          (event.evidence ?? []).map(link => `Evidence:  ${link.label} ${link.href}`),
          `Cursor:    ${result.cursor}`) : `No decision event (${result.reason}).` };
    },
  },

  triage: {
    summary: 'Every batch flight, red or isolating member, pending push and next safe member operation.',
    spec: { project: 'string' },
    async run(ctx, _positionals, options) {
      const triage = await ctx.client.request('GET', '/api/operator/batch-triage');
      const projects = (triage.projects ?? []).filter(project =>
        !options.project || String(project.project).toLowerCase() === options.project.toLowerCase());
      return { data: { ...triage, projects }, text: lines(projects.length === 0 ? 'No readable project.' : null,
        projects.map(project => lines(
          `${project.project}: batch gate ${project.batchGateEnabled ? 'on' : 'off'}, plan ${planName(project.plan)}`,
          (project.flights ?? []).map(flight =>
            `  flight ${flight.batchId} ${flight.phase}${flight.isolating ? ' (isolating)' : ''}: ${(flight.memberKeys ?? []).join(', ')}`),
          (project.members ?? []).map(member =>
            `  member ${member.taskKey} ${member.status}${member.red ? ' red' : ''}${member.isolating ? ' isolating' : ''} -> ${member.nextOperation?.operation ?? 'wait'} (${member.nextOperation?.reason ?? ''})`),
          (project.pendingPushes ?? []).map(push =>
            `  pending push ${push.jobId} ${push.ageMinutes} min${push.overdue ? ' OVERDUE' : ''}: ${push.reason ?? ''}`)))) };
    },
  },

  eject: {
    summary: 'Eject a member from its batch flight (the only member action during isolation).',
    spec: { project: 'string' },
    async run(ctx, positionals, options) {
      const card = card1(positionals);
      const project = options.project ?? (await readCard(ctx, card)).projectName;
      const answer = await ctx.client.request('POST', `/api/projects/${enc(project)}/batch-gate/members/${enc(card)}/eject`);
      return { data: { card, project, answer }, text: `Eject of ${card} requested; the batch gate ejects it on its next pass.` };
    },
  },

  continue: {
    summary: "Send a continue, steer or extend round; Studio starts it from the card's latest result ref.",
    spec: { promptFile: 'string', mode: 'string', reason: 'string', baseRef: 'string', model: 'string', cli: 'string',
      thinking: 'string', project: 'string' },
    async run(ctx, positionals, options) {
      const card = card1(positionals);
      const prompt = readText(required(options, 'promptFile', 'prompt-file'), 'prompt-file');
      if (!prompt.trim()) throw usage('empty-prompt', 'The prompt file is empty.');
      const mode = options.mode ?? 'continue';
      if (!CONTINUE_MODES.includes(mode)) throw usage('invalid-option', `--mode must be ${CONTINUE_MODES.join(', ')}.`);
      if (options.cli && !CLI_TYPES.includes(options.cli)) throw usage('invalid-option', `--cli must be ${CLI_TYPES.join(', ')}.`);
      const answer = await cardWrite(ctx, card, options.project, 'POST', '/continue', {
        prompt, mode, reason: options.reason ? checkedText(options.reason, 'reason') : undefined,
        baseRef: options.baseRef, model: options.model, cliType: options.cli, thinkingLevel: options.thinking });
      return { data: { card, answer }, text: lines(
        `${mode} round for ${card}: ${answer?.status ?? 'sent'}`,
        answer?.base ? `Run base:  ${answer.base.ref} (${answer.base.source}) ${answer.base.sha ?? ''}` : 'Run base:  current checkout (no result ref applies)') };
    },
  },

  move: {
    summary: 'Move a card to another lane (not to the accepted lane; use accept).',
    spec: { project: 'string', runIntent: 'string' },
    async run(ctx, positionals, options) {
      const card = card1(positionals);
      const lane = positionals[1];
      if (!lane) throw usage('missing-argument', 'Name the target lane, for example 2-ready.');
      if (ACCEPTED_LANES.includes(lane.toLowerCase()))
        throw usage('use-accept', 'Acceptance is its own operation: run studio-op accept, which checks the evidence first. Archiving stays with the Owner.');
      if (options.runIntent && !['revoke', 'steer'].includes(options.runIntent))
        throw usage('invalid-option', '--run-intent must be revoke or steer.');
      const answer = await cardWrite(ctx, card, options.project, 'POST', '/move',
        { targetState: lane, runIntent: options.runIntent });
      return { data: { card, lane, answer }, text: `Moved ${card} to ${lane}.` };
    },
  },

  accept: {
    summary: 'Accept a card in Human Review; refused unless the evidence is green and no decision is open.',
    spec: { project: 'string', reason: 'string' },
    async run(ctx, positionals, options) {
      const card = card1(positionals);
      const subject = await readCard(ctx, card, options.project);
      // The dedicated route reads the review artifacts fresh, not the board's cached projection.
      const projection = await ctx.client.request('GET', cardPath(card, '/review-projection'),
        { query: { project: options.project ?? subject.projectName } });
      const verdict = evaluateAcceptance(subject, projection);
      if (!verdict.acceptable)
        throw new OperationError('accept-refused',
          `${card} stays with the Owner: ${verdict.reasons.map(reason => reason.code).join(', ')}.`,
          { exitCode: EXIT.ACCEPT_REFUSED, body: { card, reasons: verdict.reasons, evidence: verdict.evidence,
            detailText: verdict.reasons.map(reason => `  - ${reason.code}: ${reason.detail}`).join('\n') } });
      const project = options.project ?? subject.projectName;
      let answer;
      try { answer = await cardWrite(ctx, card, project, 'POST', '/move', { targetState: '6-completed' }); }
      catch (error) {
        // The server's own completion check found no current content verdict: the evidence is missing.
        if (error.code === 'completion-content-gap')
          throw new OperationError('accept-refused', `${card} stays with the Owner: evidence-missing (Studio refused the completion).`,
            { exitCode: EXIT.ACCEPT_REFUSED, body: { card, evidence: verdict.evidence,
              reasons: [{ code: 'evidence-missing', detail: error.body?.error ?? error.message }],
              detailText: `  - evidence-missing: ${error.body?.error ?? error.message}` } });
        throw error;
      }
      const evidence = verdict.evidence;
      let logEntry = null;
      let logError = null;
      try {
        logEntry = await ctx.client.request('POST', '/api/operator/log', { body: {
          project, kind: 'did', cardKeys: [subject.key ?? card], auditIds: [],
          text: checkedText(`Accepted ${subject.key ?? card} as the operator verdict (OA-D5): review ${evidence.attemptId} `
            + `outcome ${evidence.outcome}, build and tests ${evidence.buildTests}, no open decision.`
            + (options.reason ? ` ${options.reason}` : ''), 'reason') } });
      } catch (error) {
        logError = error.code;
        ctx.output.note(`Accepted, but the operator log entry failed (${error.code}). Append it with studio-op log append.`);
      }
      return { data: { card, accepted: true, evidence, answer, logEntry, logError },
        text: `Accepted ${card} as the operator verdict: review ${evidence.attemptId} ${evidence.outcome}, build and tests ${evidence.buildTests}.` };
    },
  },

  create: {
    summary: 'Create a card, in Ready with --ready.',
    spec: { ready: 'boolean', project: 'string', title: 'string', promptFile: 'string', type: 'string', cli: 'string',
      id: 'string' },
    async run(ctx, _positionals, options) {
      const project = required(options, 'project');
      const title = checkedText(required(options, 'title'), 'title');
      const prompt = options.promptFile ? readText(options.promptFile, 'prompt-file') : undefined;
      if (options.type && !['bug', 'feature', 'chore'].includes(options.type))
        throw usage('invalid-option', '--type must be bug, feature or chore.');
      if (options.cli && !CLI_TYPES.includes(options.cli)) throw usage('invalid-option', `--cli must be ${CLI_TYPES.join(', ')}.`);
      const target = await watchPathFor(ctx, project);
      const answer = await ctx.client.request('POST', '/api/tasks/', { body: {
        // The create route's `project` takes an id, short code or path, not a display
        // name, so the card goes to the readable project's resolved watch path.
        id: options.id, title, watchPath: target.path,
        targetState: options.ready ? '2-ready' : '0-backlog', promptMarkdown: prompt, taskType: options.type ?? 'chore',
        ...(options.cli ? { agent: options.cli, cliType: options.cli } : {}),
        creationSource: 'operator-agent', createdBy: 'studio-op' } });
      return { data: { project: target.name, lane: options.ready ? '2-ready' : '0-backlog', answer },
        text: `Created ${answer?.taskKey ?? answer?.key ?? answer?.id ?? 'card'} in ${options.ready ? '2-ready' : '0-backlog'} of ${target.name}.` };
    },
  },

  title: {
    summary: "Repair a card's title.",
    spec: { project: 'string', title: 'string' },
    async run(ctx, positionals, options) {
      const card = card1(positionals);
      const title = checkedText(options.title ?? positionals.slice(1).join(' '), 'title').trim();
      if (!title) throw usage('missing-argument', 'Give the new title.');
      const answer = await cardWrite(ctx, card, options.project, 'PUT', '/title', { title });
      return { data: { card, title, answer }, text: `Title of ${card}: ${title}` };
    },
  },

  capacity: {
    summary: 'Quota per CLI, active fallbacks, cards waiting on quota and routing pins.',
    spec: {},
    async run(ctx) {
      const capacity = await ctx.client.request('GET', '/api/operator/capacity');
      return { data: capacity, text: lines(
        (capacity.quota ?? []).map(item =>
          `quota ${item.cliType}: ${item.exhausted ? 'EXHAUSTED' : 'available'}${item.fresh ? '' : ' (cached, not fresh)'}`),
        (capacity.activeFallbacks ?? []).map(item =>
          `fallback ${item.cardKey ?? item.project ?? 'installation'}: ${item.sourceCli ?? ''}/${item.sourceModel ?? ''} -> ${item.targetCli ?? ''}/${item.targetModel ?? ''} (${item.reason}; ${item.settingKey})`),
        (capacity.waitingCards ?? []).map(item => `waiting ${item.cardKey} (${item.cliType}) until ${item.resetAt}: ${item.reason}`),
        (capacity.modelRoutingPins ?? []).map(pinLine),
        'Capacity read.') };
    },
  },

  routing: {
    summary: 'Show central model selection, or change a review step model for every project in scope (set-step).',
    spec: {},
    async run(ctx) {
      const policy = await ctx.client.send('GET', '/api/model-selection/policy');
      const capacity = await ctx.client.request('GET', '/api/operator/capacity');
      return { data: { centralPolicy: policy.status === 200 ? policy.body : null, centralPolicyStatus: policy.status,
        modelRoutingPins: capacity.modelRoutingPins ?? [] }, text: lines(
        policy.status === 200
          ? Object.entries(policy.body?.consumers ?? {}).map(([consumer, setting]) =>
            `central ${consumer}: ${setting?.primary?.cli ?? '?'}/${setting?.primary?.model ?? 'default'}`)
          : `central model selection policy: not readable (${policy.status})`,
        (capacity.modelRoutingPins ?? []).map(pinLine)) };
    },
  },

  'routing set-step': {
    summary: 'Change one review step model through model selection, for the named projects or --all.',
    spec: { step: 'string', cli: 'string', model: 'string', thinking: 'string', pinMode: 'string', pipelineType: 'string',
      project: 'list', all: 'boolean' },
    async run(ctx, _positionals, options) {
      const stepId = required(options, 'step');
      const cliType = required(options, 'cli');
      if (!CLI_TYPES.includes(cliType)) throw usage('invalid-option', `--cli must be ${CLI_TYPES.join(', ')}.`);
      const pinMode = options.pinMode ?? 'preference';
      if (!['preference', 'hard'].includes(pinMode)) throw usage('invalid-option', '--pin-mode must be preference or hard.');
      if (Boolean(options.all) === Boolean(options.project?.length))
        throw usage('missing-option', 'Name the projects with --project (repeatable) or pass --all.');
      const pipelineType = options.pipelineType ?? 'task';
      const projects = options.all ? await readableProjects(ctx) : options.project;
      const settings = await ctx.client.send('GET', '/api/projects/settings');
      const results = [];
      for (const project of projects) {
        // The route replaces the whole step setting, so keep every other field as it is.
        const existing = stepSetting(settings.status === 200 ? settings.body : null, project, pipelineType, stepId);
        const answer = await ctx.client.send('PUT', `/api/projects/${enc(project)}/pipeline-step`, { body: {
          ...existing, pipelineType, stepId, cliType, model: options.model ?? null, thinkingLevel: options.thinking ?? null,
          pinMode } });
        results.push({ project, status: answer.status, ok: answer.status >= 200 && answer.status < 300,
          error: answer.body?.error ?? null });
      }
      const failed = results.filter(result => !result.ok);
      const data = { stepId, pipelineType, cliType, model: options.model ?? null, pinMode,
        // The per-project step override is the model selection layer an operator credential may write;
        // the central consumer policy is an installation-wide setting that stays with the Owner.
        via: 'model-selection-step-override', results };
      if (failed.length > 0)
        throw new OperationError('routing-partial',
          `${failed.length} of ${results.length} projects refused the step model change.`,
          { exitCode: failed.length === results.length ? EXIT.FAILURE : EXIT.CONFLICT, body: data });
      return { data, text: lines(results.map(result =>
        `${result.project}: ${stepId} -> ${cliType}/${options.model ?? 'default'} (${pinMode})`)) };
    },
  },

  preflight: {
    summary: 'Check before a restart or deploy: pending pushes, publishing flights, cards mid-commit.',
    spec: {},
    async run(ctx, positionals) {
      const operation = positionals[0];
      if (!PREFLIGHTS.includes(operation)) throw usage('missing-argument', 'Run studio-op preflight restart or studio-op preflight deploy.');
      const answer = await ctx.client.send('GET', `/api/operator/preflight/${operation}`);
      if (answer.status === 409) throw blocked(operation, answer.body);
      if (answer.status < 200 || answer.status >= 300)
        throw failure('GET', `/api/operator/preflight/${operation}`, answer);
      return { data: answer.body, text: `Preflight ${operation}: clear (checked ${answer.body?.checkedAtUtc ?? 'now'}). Read it again right before acting.` };
    },
  },

  'deploy plan': {
    summary: 'An ordered, expiring server-first deploy plan: drain, Task Server, readiness, Engine, hosts.',
    spec: { targetVersion: 'string', window: 'string' },
    async run(ctx, _positionals, options) {
      const targetVersion = required(options, 'targetVersion', 'target-version');
      const windowMinutes = options.window ? positiveInteger(options.window, 'window', { min: 5, max: 120 }) : 30;
      const answer = await ctx.client.send('POST', '/api/operator/deploy/plan', { body: { targetVersion, windowMinutes } });
      if (answer.status === 409) throw blocked('deploy', answer.body);
      if (answer.status < 200 || answer.status >= 300)
        throw failure('POST', '/api/operator/deploy/plan', answer);
      const plan = answer.body;
      return { data: plan, text: lines(
        `Deploy plan ${plan.id ?? ''} from ${plan.currentVersion ?? '?'} to ${plan.targetVersion ?? targetVersion}, valid until ${plan.expiresAtUtc}:`,
        (plan.steps ?? []).map(step => `  ${step.order}. ${step.kind}${step.version ? ` ${step.version}` : ''}${step.protocolVersion ? ` (protocol ${step.protocolVersion})` : ''}${step.hosts?.length ? `: ${step.hosts.map(host => `${host.hostId} ${host.version}`).join(', ')}` : ''}`),
        'Run the steps in this order inside the window; an expired plan is void.') };
    },
  },

  log: {
    summary: 'Read the operator log of a project.',
    spec: { project: 'string', after: 'string' },
    async run(ctx, _positionals, options) {
      if (options.after !== undefined && !/^\d+$/.test(options.after)) throw usage('invalid-option', '--after must be a cursor.');
      const answer = await ctx.client.request('GET', '/api/operator/log', { query: { project: options.project, after: options.after } });
      return { data: answer, text: lines((answer.entries ?? []).length === 0 ? 'No operator log entries.' : null,
        (answer.entries ?? []).map(entry =>
          `#${entry.cursor} ${entry.at ?? entry.time ?? ''} ${entry.project} ${entry.kind} ${entry.actor ?? ''}: ${entry.text}${entry.cardKeys?.length ? ` [${entry.cardKeys.join(', ')}]` : ''}`),
        `Cursor: ${answer.cursor}`) };
    },
  },

  'log append': {
    summary: 'Write down what was observed, decided or done, and why.',
    spec: { project: 'string', kind: 'string', text: 'string', textFile: 'string', card: 'list', audit: 'list' },
    async run(ctx, _positionals, options) {
      const project = required(options, 'project');
      const kind = required(options, 'kind');
      if (!LOG_KINDS.includes(kind)) throw usage('invalid-option', `--kind must be ${LOG_KINDS.join(', ')}.`);
      if (Boolean(options.text) === Boolean(options.textFile)) throw usage('missing-option', 'Give --text or --text-file.');
      const text = options.textFile ? readText(options.textFile, 'text-file') : checkedText(options.text, 'text');
      const entry = await ctx.client.request('POST', '/api/operator/log', { body: {
        project, kind, text, cardKeys: options.card ?? [], auditIds: options.audit ?? [] } });
      return { data: { entry }, text: `Logged #${entry?.cursor ?? '?'} (${kind}) in ${project}.` };
    },
  },
};

function planName(plan) {
  if (typeof plan === 'string') return plan;
  return plan?.decision ?? plan?.action ?? plan?.kind ?? 'see --json';
}

function pinLine(pin) {
  return `pin ${pin.project} ${pin.stepId ?? pin.consumer}: ${pin.cliType ?? 'any'}/${pin.model ?? 'default'} (${pin.pinMode})`;
}

function blocked(operation, body) {
  const blockers = body?.blockers ?? [];
  return new OperationError('preflight-blocked',
    `${operation} is blocked by ${blockers.length} blocker${blockers.length === 1 ? '' : 's'}: ${blockers.map(item => item.code).join(', ')}.`,
    { exitCode: EXIT.PREFLIGHT_BLOCKED, body: { operation, allowed: false, blockers,
      detailText: blockers.map(item => `  - ${item.code} ${item.project ?? ''} ${item.subject ?? ''}: ${item.detail ?? ''}`).join('\n') } });
}

/** The project's current setting for one step, minus the model fields this command sets. */
function stepSetting(all, project, pipelineType, stepId) {
  const settings = Object.entries(all ?? {}).find(([name]) => name.toLowerCase() === project.toLowerCase())?.[1];
  if (!settings) return {};
  const byType = Object.entries(settings.pipelineStepsByType ?? {})
    .find(([type]) => type.toLowerCase() === pipelineType.toLowerCase())?.[1]
    ?? (pipelineType === 'task' ? settings.pipelineSteps : null) ?? {};
  const wanted = stepId.toLowerCase();
  const found = Object.entries(byType).find(([id]) => id.toLowerCase() === wanted || id.toLowerCase().endsWith(`-${wanted}`))?.[1];
  if (!found) return {};
  const { enabled, economyModel, enrichmentBlockIds, maxIterations, mode, prompt, condition } = found;
  return { enabled, economyModel, enrichmentBlockIds, maxIterations, mode, prompt, condition };
}

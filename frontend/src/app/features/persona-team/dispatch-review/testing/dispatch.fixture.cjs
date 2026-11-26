"use strict";
const { expect } = require("@playwright/test");
const { defaultWeights } = require("../dispatch-review.model");
const workspace = { id: 'ws-demo', displayName: 'Demo workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-11-10T00:00:00Z', projects: [{ sourceType: 'local-folder', id: 'PROJ-002', displayName: 'Demo Project', shortCode: 'DEMO',
            workspaceId: 'ws-demo', color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
            storageLocation: '/tmp/demo-project', repositoryPath: null, rootPath: null, repositoryUrl: null,
            urls: [], archived: false, createdAt: '2026-11-10T00:00:00Z' }] };
const attributes = { focus: null, autonomy: 'A1', riskAppetite: 'low', thoroughness: 3, signalThreshold: 40 };
const entry = { id: 'area-warden', title: 'Area warden', aspect: 'dispatch', kind: 'dispatcher', revision: '2026.11.1', adoptableRevision: null,
    descriptor: { charter: 'Separate forecast modules.', cadence: { report: 'P7D' }, budget: { tokensPerWeek: 20000 }, attributes,
        dispatch: { strategy: { id: 'one-per-area-v1', revision: '2026.11.1' }, parameters: {}, mode: 'shadow' } },
    activation: { personaId: 'area-warden', roleId: 'dispatcher', state: 'shadow', descriptorRevision: '2026.11.1', activationRevision: 1,
        attributes, allowance: { tokensPerWeek: 20000 }, history: [] } };
const raw = (persona, mode) => ({ persona, mode, strategy: ({ 'queue-keeper': 'lane-entry-v1', 'packer': 'package-then-chain-v1', 'historian': 'ledger-learned-v1', 'surveyor': 'file-overlap-v1' })[persona] || 'one-per-area-v1',
    integratedCards: 57, acceptedCards: 57, cards: Array.from({ length: 57 }, (_, i) => `AGT-${i + 1}`), fileF1: .45, tokensEstimated: mode !== 'live',
    criteria: { conflicts: 0, tokens: 10000, rework: 0, forecast: .37, parallelism: .44, lead: 53, ownCost: null, explanation: null },
    warnings: ['Unit durations and a greedy real-file bound. Packer recut is not simulated.'] });
// Rounded Dossier examples exercise passing and gated values without invoking a scheduler.
const examples = {
    'packer': { metrics: [15.8, 10500, .15, .56, .89, 26], scores: [55, 86, 42, 56, 89, 71], value: 67, plain: 67, gate: 1 },
    'area-warden': { metrics: [12.3, 11200, .11, .52, .85, 27], scores: [65, 66, 58, 52, 85, 68], value: 65, plain: 65, gate: 1 },
    'historian': { metrics: [15.8, 11600, .14, .56, .85, 27], scores: [55, 54, 46, 56, 85, 68], value: 58, plain: 58, gate: 1 },
    'surveyor': { metrics: [19.3, 11900, .16, .56, .89, 26], scores: [45, 46, 38, 56, 89, 71], value: 52, plain: 52, gate: 1 },
    'area-warden-keyword': { metrics: [0, 10000, 0, .37, .44, 53], scores: [100, 100, 100, 37, 44, 0], value: 45, plain: 81, gate: .55 },
    'queue-keeper': { metrics: [35.1, 13500, .26, 0, 1, 15], scores: [0, 0, 0, 0, 100, 100], value: 17, plain: 17, gate: 1 },
};
const row = (persona, mode) => {
    const example = examples[persona];
    const keys = ['conflicts', 'tokens', 'rework', 'forecast', 'parallelism', 'lead'];
    return { raw: { ...raw(persona, mode), criteria: { ...Object.fromEntries(keys.map((key, i) => [key, example.metrics[i]])), ownCost: null, explanation: null } },
        scores: { ...Object.fromEntries(keys.map((key, i) => [key, example.scores[i]])), ownCost: null, explanation: null },
        value: example.value, plainValue: example.plain, gateMultiplier: example.gate, status: 'scored' };
};
async function setup(page, owner = true) {
    let failLoad = false, failSave = false, small = false;
    const writes = [];
    const revisions = [{ id: 'w1', author: 'platform', at: '2026-11-17T00:00:00Z', reason: 'Optimistic defaults', weights: defaultWeights(), gate: .8 }];
    await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill({ json: {
            negotiateVersion: 1, connectionId: 'dispatch-mock', connectionToken: 'dispatch-mock',
            availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }]
        } }));
    await page.routeWebSocket('**/hubs/v1/studio**', socket => socket.onMessage(message => {
        if (typeof message === 'string' && message.includes('"protocol":"json"'))
            socket.send('{}\u001e');
    }));
    await page.route('**/healthz', route => route.fulfill({ json: { status: 'Healthy' } }));
    await page.route('**/api/**', route => {
        const url = new URL(route.request().url()), path = url.pathname;
        const json = (body) => route.fulfill({ json: body });
        if (path.endsWith('/auth/status'))
            return json({ profile: 'local', bootstrapRequired: false, authenticated: true,
                user: { id: 'owner', username: 'owner', displayName: 'Owner', role: owner ? 'owner' : 'viewer', projects: ['*'], disabled: false, mustChangePassword: false } });
        if (['/api/v1/workspaces', '/api/workspaces'].includes(path))
            return json([workspace]);
        if (path === '/api/v1/projects')
            return json(workspace.projects);
        if (path.startsWith('/api/v1/studio/board'))
            return json({ backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [], gitStateAt: null, stale: false });
        if (path.endsWith('/dispatch/review/weights')) {
            writes.push(route.request().postDataJSON());
            if (failSave)
                return route.fulfill({ status: 500, json: { error: 'Save failed' } });
            const body = route.request().postDataJSON();
            const revision = { id: 'w2', author: 'owner', at: '2026-11-26T00:00:00Z', ...body };
            revisions.push(revision);
            return json(revision);
        }
        if (path.endsWith('/dispatch/review')) {
            if (failLoad)
                return route.fulfill({ status: 500, json: { error: 'Load failed' } });
            const personas = [row('packer', 'replay'), row('area-warden', 'replay'), row('historian', 'replay'),
                row('surveyor', 'shadow'), row('area-warden-keyword', 'replay'), row('queue-keeper', 'live')];
            if (small)
                for (const r of personas)
                    Object.assign(r, { value: null, plainValue: null, status: 'too few cards', raw: { ...r.raw, integratedCards: 12, acceptedCards: 12, cards: r.raw.cards.slice(0, 12) } });
            return json({ project: 'Demo Project', from: '2026-11-01T00:00:00Z', to: '2026-12-01T00:00:00Z',
                weights: revisions.find(r => r.id === url.searchParams.get('weights')) ?? revisions.at(-1), weightRevisions: revisions,
                personas, observedPersonas: [raw('queue-keeper', 'live')], competition: [{ champion: 'queue-keeper', challenger: 'area-warden',
                        facts: { shadowIntegratedCards: 57, shadowWaves: 1, alternatingLiveWaves: 0, guardCards: 0 },
                        evaluation: { eligibleForAlternatingLive: false, championEligible: false, guardExceeded: false } }] });
        }
        if (path.endsWith('/dispatch/conflicts'))
            return json([{ id: 'conflict-1', card: 'AGT-1', partnerCards: ['AGT-2'], kind: 'merge',
                    conflictedFiles: ['backend/Features/Dispatch/DispatchReviewService.cs'], at: '2026-11-18T00:00:00Z', personaId: 'queue-keeper', personaMode: 'live',
                    dispatchDecisionIds: { 'AGT-1': 'decision-1', 'AGT-2': 'decision-2' }, backfilled: false }]);
        if (path.includes('/personas')) {
            if (path.endsWith('/budget'))
                return json({ poolTokens: 20000, usedTokens: 0, startUtc: '', endUtc: '', personas: {} });
            if (path.endsWith('/sheet'))
                return json({ windows: [], revisions: [] });
            if (/\/(items|reports|ratings|issues|decks)$/.test(path))
                return json([]);
            return json({ personas: [entry] });
        }
        if (path === '/api/deploy/run-hosts') return json([]);
        if (path.endsWith('/runner/status'))
            return json({ projects: {} });
        if (path === '/api/cli/usage')
            return json({ at: new Date().toISOString(), sessions: [] });
        if (path === '/api/usage/cockpit')
            return json({ workspaceId: 'ws-demo', clis: [], cost: null });
        if (path.endsWith('/cli/quota'))
            return json({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] });
        if (path.endsWith('/quota/admission'))
            return json({ workspaceId: 'ws-demo', projects: [], workspace: { status: 'ok', codingAllowed: true, hostingAllowed: true, limited: false } });
        if (path.startsWith('/api/tasks/archive'))
            return json({ items: [], total: 0, offset: 0, limit: 0 });
        if ((path.startsWith('/api/v1/management/') && !path.endsWith('/credential-inventory')) || /\/(tags|clients|watch-paths|tasks|jobs)\/?$/.test(path) || path.startsWith('/api/bus/'))
            return json([]);
        return json({});
    });
    await page.goto('/#/workspace/team/dispatch%3APROJ-002');
    await expect(page.getByTestId('dispatch-ranking')).toBeVisible();
    return { writes, failLoad: (value) => failLoad = value, failSave: (value) => failSave = value, small: () => small = true };
}

module.exports = { setup };

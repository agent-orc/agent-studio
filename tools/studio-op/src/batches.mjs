import fs from 'node:fs';
import { usage } from './args.mjs';
import { isTokenShaped } from './credential.mjs';

const PATH = '/api/auth/operator-session/batches';

/** Submit typed items and read Studio's result. Only the Owner confirms in Studio. */
export async function submitBatch(ctx, options, kind) {
  let items;
  try {
    if (fs.statSync(options.batch).size > 64 * 1024) throw new Error('size');
    const text = fs.readFileSync(options.batch, 'utf8');
    if (isTokenShaped(text)) throw new Error('credential');
    items = JSON.parse(text);
  } catch {
    throw usage('batch-file-invalid', '--batch needs a JSON array file up to 64 KB without credentials.');
  }
  if (!Array.isArray(items) || items.length < 1 || items.length > 25)
    throw usage('batch-size-invalid', 'A batch contains 1 to 25 typed items.');
  const targetLane = kind === 'accept' ? '6-completed' : '7-archive';
  for (const item of items) {
    if (!item || typeof item.card !== 'string' || typeof item.reason !== 'string' || !item.reason.trim())
      throw usage('batch-item-invalid', 'Every item needs card and reason strings. Acceptances also need reservation.');
    if (kind === 'accept' && (typeof item.reservation !== 'string' || !item.reservation.trim()))
      throw usage('batch-item-invalid', 'Every acceptance needs the review reservation in one line.');
    if (item.targetLane && item.targetLane !== targetLane)
      throw usage('batch-target-invalid', `This command only targets ${targetLane}.`);
  }
  let batch = await ctx.client.request('POST', PATH, { body: {
    class: kind === 'accept' ? 'cards.accept-override' : 'cards.archive-override',
    instructionSource: options.source ?? 'unstated',
    items: items.map(item => ({ ...item, targetLane, project: item.project ?? options.project })),
  } });
  ctx.output?.note(`Owner confirmation: ${ctx.url}/#/workspace/settings/access?batch=${encodeURIComponent(batch.id)}`);
  while (['pending', 'executing'].includes(batch.state)) {
    await (ctx.waitForBatchPoll ?? (ms => new Promise(resolve => setTimeout(resolve, ms))))(5000);
    batch = await ctx.client.request('GET', `${PATH}/${encodeURIComponent(batch.id)}`);
  }
  return { data: batch, text: `Batch ${batch.id}: ${batch.state}.\n` + batch.items.map(item =>
    `${item.request.card}: ${item.state}, ${item.resultReason ?? item.refusal ?? 'waiting'}`).join('\n') };
}

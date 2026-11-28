import { systemClock } from '../../scripts/release/product-scenario/runner.mjs';

export async function waitForCompletion(readCard, noteLane, { clock = systemClock, intervalMs = 2000, timeoutMs = 120000 } = {}) {
  const deadline = clock.now().getTime() + timeoutMs;
  let lastLane;
  for (;;) {
    const card = await readCard();
    if (card.state !== lastLane) noteLane(card.state);
    lastLane = card.state;
    if (card.state === '6-completed') return card;
    if (['3a-failed-pickup', '3b-code-not-complete', '5e-escalated', '7-archive'].includes(card.state))
      throw Error(card.key + ' stopped in ' + card.state + '.');
    const remaining = deadline - clock.now().getTime();
    if (remaining <= 0)
      throw Error('Timed out after ' + timeoutMs / 1000 + ' s waiting for ' + card.key + ' to complete. Last observed lane ' + card.state + '.');
    await clock.sleep(Math.min(intervalMs, remaining));
  }
}

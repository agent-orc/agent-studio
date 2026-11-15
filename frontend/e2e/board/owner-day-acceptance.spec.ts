import { test, expect } from '@playwright/test';
import { dismissDevErrorDialog } from '../helpers/theme';

// Runs only with PW_E2E_PROFILE=owner-day. The prebuilt server seeds a
// disposable TaskRepository and has no live runner or external Git origin.
test('owner-day cards are accepted by real board clicks', async ({ page }, testInfo) => {
  test.skip(process.env.PW_E2E_PROFILE !== 'owner-day', 'Requires the isolated owner-day profile.');
  test.setTimeout(180_000);
  const getReadiness = () => page.request.post('/api/tasks/acceptance-readiness', {
    data: { project: 'Demo App' }, headers: { 'X-Client-Id': 'local-default' },
  });
  await expect.poll(async () => {
    const response = await getReadiness();
    return response.ok() ? (await response.json()).items.length : 0;
  }, { timeout: 30_000 }).toBeGreaterThanOrEqual(5);
  const readiness = await getReadiness();
  expect(readiness.ok(), await readiness.text()).toBeTruthy();
  const report = await readiness.json();
  for (const key of ['DEMO-5', 'DEMO-6', 'DEMO-14']) {
    const item = report.items.find((candidate: { key: string }) => candidate.key === key);
    expect(item, `${key}: ${JSON.stringify(report.items.map((candidate: { key: string | null }) => candidate.key))}`)
      .toMatchObject({ readiness: 'ready', stage: 'admitted' });
  }
  const refused = report.items.find((candidate: { key: string }) => candidate.key === 'DEMO-19');
  expect(refused).toMatchObject({ readiness: 'review-concerns', stage: 'content' });
  expect(refused.reason).toContain('result SHA');

  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
    }));
  });
  await page.goto('/?includeFixtures=true');
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('acceptance-readiness-summary')).toHaveText(/3\s+of\s+6\s+ready to accept/, { timeout: 30_000 });
  const concern = page.getByTestId('lane-5-human-review')
    .locator('[data-testid="task-card"]', { hasText: 'Resolve the refused completion claim' }).first();
  await expect(concern.getByTestId('acceptance-readiness-note')).toHaveAttribute('data-readiness', 'review-concerns');
  await expect(concern.getByTestId('acceptance-send-round')).toBeVisible();
  const before = testInfo.outputPath('owner-day-acceptance-before--real.png');
  await page.screenshot({ path: before });
  await testInfo.attach('owner-day-acceptance-before--real', { path: before, contentType: 'image/png' });

  await page.getByTestId('acceptance-accept-ready').click();
  await expect(page.getByTestId('confirm-dialog')).toContainText('Accept 3 ready cards?');
  await page.getByTestId('confirm-dialog-confirm').click();
  await expect(page.getByTestId('acceptance-results')).toContainText('3 of 3 accepted', { timeout: 30_000 });
  await expect(page.getByTestId('acceptance-result')).toHaveCount(3);
  const after = testInfo.outputPath('owner-day-acceptance-after--real.png');
  await page.screenshot({ path: after });
  await testInfo.attach('owner-day-acceptance-after--real', { path: after, contentType: 'image/png' });
});

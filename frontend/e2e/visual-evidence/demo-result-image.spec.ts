import { expect, test } from '@playwright/test';

const CARD = '[data-testid="task-card"], [data-testid="job-card"]';

test('DEMO-9 Result image loads inline and opens in the lightbox', async ({ page }, testInfo) => {
  expect(process.env.PW_E2E_PROFILE).toBe('demo');
  const imageResponses: { url: string; status: number }[] = [];
  page.on('response', (response) => {
    if (response.url().includes('reports-export--real.png')) {
      imageResponses.push({ url: response.url(), status: response.status() });
    }
  });

  await page.goto('/');
  const picker = page.getByTestId('studio-project-picker-trigger');
  await expect(picker).toBeVisible();
  await picker.click();
  await page.getByTestId('studio-project-picker-panel').getByText('Demo App', { exact: true }).click();
  const card = page.locator(CARD).filter({ hasText: 'DEMO-9' }).first();
  await expect(card).toBeVisible();
  await card.click();
  await page.getByTestId('inspector-tab-protocol').click();

  const result = page.getByTestId('protocol-beautiful-results');
  const image = result.locator('img[src*="reports-export--real.png"]');
  await expect(image).toBeVisible();
  await expect.poll(() => image.evaluate((element: HTMLImageElement) => element.complete && element.naturalWidth > 0)).toBe(true);
  expect(imageResponses.length).toBeGreaterThan(0);
  expect(imageResponses.every((response) => response.status === 200), JSON.stringify(imageResponses)).toBe(true);
  await result.getByRole('button', { name: 'Open image' }).dispatchEvent('click');
  const lightbox = page.getByTestId('media-lightbox');
  await expect(lightbox).toBeVisible();
  const lightboxImage = page.getByTestId('media-lightbox-image');
  await expect.poll(() => lightboxImage.evaluate((element: HTMLImageElement) => element.complete && element.naturalWidth > 0)).toBe(true);
  const screenshotPath = testInfo.outputPath('demo-9-lightbox--real.png');
  await page.screenshot({ path: screenshotPath });
  await testInfo.attach('demo-9-lightbox', { path: screenshotPath, contentType: 'image/png' });
});

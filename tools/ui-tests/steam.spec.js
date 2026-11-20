const { test, expect } = require('@playwright/test');

test('Steam friends show avatars, presence, last seen, and recent activity', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.addInitScript(() => {
    const create = URL.createObjectURL.bind(URL);
    window.__steamAvatarUrlCount = 0;
    URL.createObjectURL = (...args) => {
      window.__steamAvatarUrlCount++;
      return create(...args);
    };
  });
  await page.goto('/panel/');
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null') || {};
    const pages = [{ id: 'steam', title: 'Steam', icon: '🎮', enabled: true, widgets: [
      { id: 'steam-friends', type: 'steam', x: 0, y: 0, w: 6, h: 5, options: { offline: true } },
      { id: 'steam-activity', type: 'steam-activity', x: 6, y: 0, w: 6, h: 4, options: {} },
    ] }];
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, pages, shortcuts: cfg.shortcuts || [] }));
  });
  await page.reload();

  const friends = page.locator('.w-steam');
  await expect(friends.locator('.st-row')).toHaveCount(2);
  await expect(friends.locator('.st-row').nth(0)).toHaveClass(/\bonline\b/);
  await expect(friends.locator('.st-row').nth(0)).toHaveClass(/\bstate-1\b/);
  await expect(friends.locator('.st-row').nth(1)).toHaveClass(/\bstate-0\b/);
  await expect(friends.locator('.st-row').nth(1)).not.toHaveClass(/\bonline\b/);
  await expect(friends.locator('.st-name').first()).toHaveText('Ada');
  await expect(friends.locator('.st-sub').first()).toHaveText('Playing Hades II');
  await expect(friends.locator('.st-sub').nth(1)).toContainText('Last seen');
  const avatar = friends.locator('.st-avatar').first();
  await expect(avatar).toHaveAttribute('src', /^blob:/);
  await expect.poll(() => avatar.evaluate((image) => image.naturalWidth)).toBeGreaterThan(0);

  const activity = page.locator('.w-steam-activity');
  await expect(activity.locator('.st-event')).toHaveCount(1);
  await expect(activity.locator('.st-event .st-name')).toHaveText('Ada');
  await expect(activity.locator('.st-event .st-sub')).toHaveText('Started playing Hades II');
  await expect(activity.locator('.st-avatar')).toHaveCount(1);
  await expect.poll(() => page.evaluate(() => window.__steamAvatarUrlCount)).toBe(2);
  expect(errors).toEqual([]);
});

import { defineConfig, devices } from '@playwright/test'

const baseURL = process.env.DEMO_BASE_URL
if (!baseURL || !/^http:\/\/127\.0\.0\.1:5092$/.test(baseURL))
  throw new Error('Use node scripts/Demo.mjs --test; browser tests require the isolated demo host.')

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/setup.ts',
  workers: 1,
  retries: 0,
  timeout: 45_000,
  reporter: [['list'], ['html', { outputFolder: '../artifacts/browser/report', open: 'never' }]],
  outputDir: '../artifacts/browser/results',
  use: {
    ...devices['Desktop Chrome'],
    baseURL,
    storageState: '../artifacts/browser/auth/customer.json',
    screenshot: 'only-on-failure',
    launchOptions: { slowMo: process.env.DEMO_RECORD === '1' ? 500 : 0 },
    video: { mode: process.env.DEMO_RECORD === '1' ? 'on' : 'retain-on-failure', size: { width: 1280, height: 720 } },
    trace: 'retain-on-failure',
  },
})

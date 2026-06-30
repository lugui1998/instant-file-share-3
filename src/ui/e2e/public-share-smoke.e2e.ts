import { expect, test } from '@playwright/test'
import fs from 'node:fs/promises'
import path from 'node:path'
import { startAgentE2EHost, type AgentE2EHost } from './agent-host'

let host: AgentE2EHost | null = null

test.beforeAll(async () => {
  host = await startAgentE2EHost()
})

test.afterAll(async () => {
  await host?.stop()
  host = null
})

test('uploads a file through a receive link', async ({ page }) => {
  expect(host).not.toBeNull()
  const currentHost = host!
  const uploadFixturePath = path.join(currentHost.rootPath, 'upload-smoke.txt')
  await fs.writeFile(uploadFixturePath, 'upload smoke from browser', 'utf8')

  await page.goto(currentHost.receiveUrl)
  await expect(page.getByRole('heading', { name: 'Upload to E2E Host' })).toBeVisible()

  await page.locator('input[type="file"]').setInputFiles(uploadFixturePath)
  await expect(page.getByLabel('Uploaded')).toBeVisible()
  await expect(page.getByText('upload-smoke.txt')).toBeVisible()

  await expect.poll(async () => {
    return await fs.readFile(path.join(currentHost.receiveDirectory, 'upload-smoke.txt'), 'utf8')
  }).toBe('upload smoke from browser')
})

test('downloads a shared file through the public file route', async ({ page }) => {
  expect(host).not.toBeNull()
  const currentHost = host!
  await page.goto(currentHost.downloadUrl)
  await expect(page.getByRole('heading', { level: 1, name: 'download-smoke.txt' })).toBeVisible()

  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download in browser' }).click()
  const download = await downloadPromise
  const downloadedPath = await download.path()

  expect(download.suggestedFilename()).toBe('download-smoke.txt')
  expect(downloadedPath).toBeTruthy()
  await expect.poll(async () => {
    return await fs.readFile(downloadedPath!, 'utf8')
  }).toBe('download smoke from e2e host')
})

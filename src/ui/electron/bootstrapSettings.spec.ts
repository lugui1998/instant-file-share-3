// @vitest-environment node

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { readBootstrapLocalApiPort, resolveBootstrapSettingsPath } from './bootstrapSettings'

describe('bootstrapSettings', () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'ifs-bootstrap-settings-'))

  afterEach(() => {
    fs.rmSync(tempRoot, { recursive: true, force: true })
    fs.mkdirSync(tempRoot, { recursive: true })
  })

  it('resolves the bootstrap settings path inside LocalAppData', () => {
    expect(resolveBootstrapSettingsPath('C:\\Temp\\LocalAppData')).toBe('C:\\Temp\\LocalAppData\\InstantFileShare\\bootstrap-settings.json')
  })

  it('reads the configured local API port from the bootstrap snapshot', () => {
    const snapshotPath = resolveBootstrapSettingsPath(tempRoot)
    fs.mkdirSync(path.dirname(snapshotPath), { recursive: true })
    fs.writeFileSync(snapshotPath, JSON.stringify({ localApiPort: 46435 }))

    expect(readBootstrapLocalApiPort(tempRoot)).toBe(46435)
  })

  it('falls back to the default local API port when the snapshot is missing', () => {
    expect(readBootstrapLocalApiPort(tempRoot)).toBe(46430)
  })
})

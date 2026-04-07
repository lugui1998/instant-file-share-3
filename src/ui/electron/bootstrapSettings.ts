import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { normalizeLocalApiPort } from '../shared/agentEndpoint.js'

type BootstrapSettingsSnapshot = {
  localApiPort?: number
}

export function resolveBootstrapSettingsPath(localAppData = process.env.LOCALAPPDATA) {
  const baseDirectory = localAppData && localAppData.trim().length > 0
    ? localAppData
    : path.join(os.homedir(), 'AppData', 'Local')

  return path.join(baseDirectory, 'InstantFileShare', 'bootstrap-settings.json')
}

export function readBootstrapLocalApiPort(localAppData = process.env.LOCALAPPDATA) {
  const snapshotPath = resolveBootstrapSettingsPath(localAppData)

  try {
    const raw = fs.readFileSync(snapshotPath, 'utf8')
    const snapshot = JSON.parse(raw) as BootstrapSettingsSnapshot
    return normalizeLocalApiPort(snapshot.localApiPort)
  } catch {
    return normalizeLocalApiPort(undefined)
  }
}

import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import fs from 'node:fs/promises'
import path from 'node:path'

export type AgentE2EHostInfo = {
  publicBaseUrl: string
  localBaseUrl: string
  receiveUrl: string
  downloadUrl: string
  rootPath: string
  receiveDirectory: string
  downloadFilePath: string
}

export type AgentE2EHost = AgentE2EHostInfo & {
  stop: () => Promise<void>
}

export async function startAgentE2EHost(): Promise<AgentE2EHost> {
  const uiRoot = process.cwd()
  const repositoryRoot = path.resolve(uiRoot, '..', '..')
  const publicShareAssetsDirectory = path.join(uiRoot, 'dist-public-share')
  const tempRoot = path.join(repositoryRoot, '.tmp', 'e2e')
  await fs.mkdir(tempRoot, { recursive: true })

  const child = spawn(
    'dotnet',
    [
      'run',
      '--project',
      path.join(repositoryRoot, 'tests', 'InstantFileShare.E2EHost', 'InstantFileShare.E2EHost.csproj'),
      '--',
      '--repository-root',
      repositoryRoot,
      '--public-share-assets',
      publicShareAssetsDirectory,
      '--temp-root',
      tempRoot,
    ],
    {
      cwd: repositoryRoot,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    },
  )

  const stderr: string[] = []
  child.stderr.on('data', (chunk) => stderr.push(chunk.toString()))

  let info: AgentE2EHostInfo
  try {
    info = await waitForReadyLine(child, stderr)
  } catch (error) {
    await stopHost(child)
    throw error
  }

  return {
    ...info,
    stop: async () => {
      await stopHost(child)
      await cleanupHostRoot(info.rootPath)
    },
  }
}

async function waitForReadyLine(
  child: ChildProcessWithoutNullStreams,
  stderr: string[],
): Promise<AgentE2EHostInfo> {
  return await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      void stopHost(child).finally(() => reject(new Error(`Timed out waiting for E2E host readiness.\n${stderr.join('')}`)))
    }, 45_000)

    let stdout = ''

    child.once('exit', (code, signal) => {
      clearTimeout(timeout)
      reject(new Error(`E2E host exited before readiness. code=${code} signal=${signal}\n${stderr.join('')}`))
    })

    child.once('error', (error) => {
      clearTimeout(timeout)
      reject(error)
    })

    child.stdout.on('data', (chunk) => {
      stdout += chunk.toString()
      const lines = stdout.split(/\r?\n/)
      stdout = lines.pop() ?? ''

      for (const line of lines) {
        const trimmed = line.trim()
        if (!trimmed.startsWith('{')) {
          continue
        }

        const readyInfo = parseReadyLine(trimmed)
        if (readyInfo === null) {
          continue
        }

        clearTimeout(timeout)
        resolve(readyInfo)
      }
    })
  })
}

export function parseReadyLine(line: string): AgentE2EHostInfo | null {
  const trimmed = line.trim()
  if (!trimmed.startsWith('{')) {
    return null
  }

  let payload: unknown
  try {
    payload = JSON.parse(trimmed)
  } catch {
    return null
  }

  if (!isRecord(payload)) {
    return null
  }

  const readyInfo = {
    publicBaseUrl: payload.publicBaseUrl,
    localBaseUrl: payload.localBaseUrl,
    receiveUrl: payload.receiveUrl,
    downloadUrl: payload.downloadUrl,
    rootPath: payload.rootPath,
    receiveDirectory: payload.receiveDirectory,
    downloadFilePath: payload.downloadFilePath,
  }

  return Object.values(readyInfo).every((value) => typeof value === 'string')
    ? (readyInfo as AgentE2EHostInfo)
    : null
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

async function cleanupHostRoot(rootPath: string): Promise<void> {
  if (process.env.IFS_E2E_KEEP_TEMP === '1') {
    return
  }

  await fs.rm(rootPath, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 })
}

async function stopHost(child: ChildProcessWithoutNullStreams): Promise<void> {
  if (child.exitCode !== null || child.signalCode !== null) {
    return
  }

  child.stdin.write('stop\n', () => {
    child.stdin.end()
  })
  await new Promise<void>((resolve) => {
    const timeout = setTimeout(() => {
      if (child.exitCode === null && child.signalCode === null) {
        child.kill()
      }

      resolve()
    }, 10_000)

    child.once('exit', () => {
      clearTimeout(timeout)
      resolve()
    })
  })
}

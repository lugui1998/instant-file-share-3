import { describe, expect, it } from 'vitest'
import { parseReadyLine } from './agent-host'

const readyInfo = {
  publicBaseUrl: 'http://127.0.0.1:5001',
  localBaseUrl: 'http://127.0.0.1:5002',
  receiveUrl: 'http://127.0.0.1:5001/s/receive',
  downloadUrl: 'http://127.0.0.1:5001/s/download',
  rootPath: 'C:\\Temp\\ifs-e2e',
  receiveDirectory: 'C:\\Temp\\ifs-e2e\\receive',
  downloadFilePath: 'C:\\Temp\\ifs-e2e\\download-smoke.txt',
}

describe('parseReadyLine', () => {
  it('parses a complete readiness payload', () => {
    expect(parseReadyLine(JSON.stringify(readyInfo))).toEqual(readyInfo)
  })

  it('ignores non-readiness JSON payloads', () => {
    expect(parseReadyLine('{"event":"started"}')).toBeNull()
  })

  it('ignores malformed JSON-like output', () => {
    expect(parseReadyLine('{"event":')).toBeNull()
  })
})

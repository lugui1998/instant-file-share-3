import { describe, expect, it } from 'vitest'
import { buildAgentBaseUrl, buildRuntimeSocketUrl, createAgentEndpointState, normalizeLocalApiPort } from './agentEndpoint'

describe('agentEndpoint', () => {
  it('normalizes invalid local API ports to the default port', () => {
    expect(normalizeLocalApiPort(undefined)).toBe(46430)
    expect(normalizeLocalApiPort(0)).toBe(46430)
    expect(normalizeLocalApiPort(70000)).toBe(46430)
  })

  it('builds local agent URLs from the configured port', () => {
    expect(buildAgentBaseUrl(46431)).toBe('http://127.0.0.1:46431')
    expect(buildRuntimeSocketUrl('http://127.0.0.1:46431')).toBe('ws://127.0.0.1:46431/ws/runtime')
  })

  it('updates the endpoint state when the local API port changes', () => {
    const endpoint = createAgentEndpointState(46430)

    endpoint.setLocalApiPort(46432)

    expect(endpoint.getLocalApiPort()).toBe(46432)
    expect(endpoint.getAgentBaseUrl()).toBe('http://127.0.0.1:46432')
    expect(endpoint.getRuntimeSocketUrl()).toBe('ws://127.0.0.1:46432/ws/runtime')
  })
})

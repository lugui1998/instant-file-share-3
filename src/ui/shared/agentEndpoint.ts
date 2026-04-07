const DEFAULT_LOCAL_API_PORT = 46430

export function normalizeLocalApiPort(value: unknown) {
  if (typeof value !== 'number' || !Number.isInteger(value) || value < 1 || value > 65535) {
    return DEFAULT_LOCAL_API_PORT
  }

  return value
}

export function buildAgentBaseUrl(localApiPort: number) {
  return `http://127.0.0.1:${normalizeLocalApiPort(localApiPort)}`
}

export function buildRuntimeSocketUrl(agentBaseUrl: string) {
  return `${agentBaseUrl.replace(/^http/i, 'ws')}/ws/runtime`
}

export function createAgentEndpointState(initialLocalApiPort = DEFAULT_LOCAL_API_PORT) {
  let localApiPort = normalizeLocalApiPort(initialLocalApiPort)

  return {
    getLocalApiPort: () => localApiPort,
    getAgentBaseUrl: () => buildAgentBaseUrl(localApiPort),
    getRuntimeSocketUrl: () => buildRuntimeSocketUrl(buildAgentBaseUrl(localApiPort)),
    setLocalApiPort: (nextLocalApiPort: number) => {
      localApiPort = normalizeLocalApiPort(nextLocalApiPort)
    },
  }
}

import { afterEach, describe, expect, it, vi } from 'vitest'
import { createBrowserBridge } from './agentBridge'

class FakeWebSocket {
  public static instances: FakeWebSocket[] = []
  public readonly url: string
  private readonly listeners = new Map<string, Array<(event?: Event | MessageEvent) => void>>()

  constructor(url: string) {
    this.url = url
    FakeWebSocket.instances.push(this)
  }

  addEventListener(type: string, listener: (event?: Event | MessageEvent) => void) {
    const listeners = this.listeners.get(type) ?? []
    listeners.push(listener)
    this.listeners.set(type, listeners)
  }

  close() {
    this.emit('close')
  }

  emit(type: string, event?: Event | MessageEvent) {
    for (const listener of this.listeners.get(type) ?? []) {
      listener(event)
    }
  }
}

describe('createBrowserBridge', () => {
  const originalFetch = globalThis.fetch
  const originalWebSocket = globalThis.WebSocket

  afterEach(() => {
    vi.useRealTimers()
    globalThis.fetch = originalFetch
    globalThis.WebSocket = originalWebSocket
    FakeWebSocket.instances = []
    vi.restoreAllMocks()
  })

  it('switches future REST requests to the saved local API port', async () => {
    const fetchMock = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ shares: [], transfers: [], settings: { localApiPort: 46435 }, cloudflared: {} }), { status: 200 }))

    globalThis.fetch = fetchMock

    const bridge = createBrowserBridge()
    await bridge.saveSettings({ localApiPort: 46435 })
    await bridge.getRuntime()

    expect(fetchMock.mock.calls[0]?.[0]).toBe('http://127.0.0.1:46430/api/settings')
    expect(fetchMock.mock.calls[1]?.[0]).toBe('http://127.0.0.1:46435/api/runtime')
  })

  it('reconnects runtime sockets against the latest saved local API port', async () => {
    vi.useFakeTimers()
    globalThis.fetch = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 204 }))
    globalThis.WebSocket = FakeWebSocket as unknown as typeof WebSocket

    const bridge = createBrowserBridge()
    const disconnect = bridge.connectRuntime(() => {})

    await bridge.saveSettings({ localApiPort: 46436 })

    FakeWebSocket.instances[0]?.emit('close')
    await vi.advanceTimersByTimeAsync(2000)

    expect(FakeWebSocket.instances.map((socket) => socket.url)).toEqual([
      'ws://127.0.0.1:46430/ws/runtime',
      'ws://127.0.0.1:46436/ws/runtime',
    ])

    disconnect()
  })

  it('posts to the show in explorer endpoint for the selected share', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 204 }))
    globalThis.fetch = fetchMock

    const bridge = createBrowserBridge()
    await bridge.showShareInExplorer('share-123')

    expect(fetchMock).toHaveBeenCalledWith(
      'http://127.0.0.1:46430/api/shares/share-123/show-in-explorer',
      expect.objectContaining({
        method: 'POST',
      }),
    )
  })
})

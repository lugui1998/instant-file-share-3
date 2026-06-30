import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import FolderSharePage from './FolderSharePage.vue'
import type { PublicShareFolderModel, PublicSharePageModel } from '../../types'

class FakeWebSocket {
  static instances: FakeWebSocket[] = []

  readonly url: string
  private listeners = new Map<string, Array<() => void>>()

  constructor(url: string) {
    this.url = url
    FakeWebSocket.instances.push(this)
  }

  addEventListener(type: string, listener: () => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }

  close() {}

  emit(type: string) {
    for (const listener of this.listeners.get(type) ?? []) {
      listener()
    }
  }
}

describe('FolderSharePage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    FakeWebSocket.instances = []
  })

  it('refreshes the folder listing after a WebSocket change notification', async () => {
    window.history.pushState({}, '', '/s/folder-token/docs')
    vi.stubGlobal('WebSocket', FakeWebSocket)
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => createFolder(['fresh.txt']),
    })
    vi.stubGlobal('fetch', fetchMock)

    const wrapper = mount(FolderSharePage, {
      props: {
        page: createPage(),
        folder: createFolder(['stale.txt']),
      },
    })

    expect(FakeWebSocket.instances).toHaveLength(1)
    expect(FakeWebSocket.instances[0].url).toBe(`${toWebSocketOrigin(window.location.origin)}/s/folder-token/docs?ifs=folder-events`)
    expect(wrapper.text()).toContain('stale.txt')

    FakeWebSocket.instances[0].emit('message')
    await vi.waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        `${window.location.origin}/s/folder-token/docs?ifs=folder-list`,
        expect.objectContaining({
          cache: 'no-store',
        }),
      )
      expect(wrapper.text()).toContain('fresh.txt')
    })
    expect(wrapper.text()).not.toContain('stale.txt')
  })

  it('renders file size before last modified in the folder table', () => {
    const wrapper = mount(FolderSharePage, {
      props: {
        page: createPage(),
        folder: createFolder(['report.pdf']),
      },
    })

    expect(wrapper.findAll('thead th').map((header) => header.text())).toEqual([
      'Name',
      'Size',
      'Modified',
    ])
    expect(wrapper.findAll('tbody tr:first-child td').map((cell) => cell.text())).toEqual([
      'report.pdf',
      '1 B',
      '6/29/2026 2:00 PM',
    ])
  })
})

function createPage(): PublicSharePageModel {
  return {
    kind: 'folder',
    title: 'Browse files',
    description: 'Browse files.',
    canonicalUrl: 'http://internal.example/s/folder-token/team-files/docs',
    siteName: 'Instant File Share',
    repositoryUrl: 'https://github.com/lugui1998/instant-file-share-3',
    folder: createFolder(['stale.txt']),
  }
}

function toWebSocketOrigin(origin: string) {
  const url = new URL(origin)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  return url.toString().replace(/\/$/, '')
}

function createFolder(fileNames: string[]): PublicShareFolderModel {
  return {
    name: 'team-files',
    relativePath: 'docs',
    canDownloadAll: true,
    downloadAllUrl: 'https://public.example/s/folder-token/docs?download=zip',
    breadcrumbs: [
      { label: 'team-files', href: '/s/folder-token' },
      { label: 'docs', href: '/s/folder-token/docs' },
    ],
    entries: fileNames.map((fileName) => ({
      name: fileName,
      href: `/s/folder-token/docs/${fileName}`,
      isDirectory: false,
      modifiedAtLabel: '6/29/2026 2:00 PM',
      sizeLabel: '1 B',
      isParentDirectory: false,
    })),
    isEmpty: fileNames.length === 0,
  }
}

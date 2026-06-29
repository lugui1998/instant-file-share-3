import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ReceiveSharePage from './ReceiveSharePage.vue'
import type { PublicSharePageModel, PublicShareReceiveModel } from '../../types'

class FakeUploadTarget {
  private listeners = new Map<string, Array<(event: ProgressEvent) => void>>()

  addEventListener(type: string, listener: (event: ProgressEvent) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }

  emit(type: string, event: ProgressEvent) {
    for (const listener of this.listeners.get(type) ?? []) {
      listener(event)
    }
  }
}

class FakeXMLHttpRequest {
  static instances: FakeXMLHttpRequest[] = []

  readonly upload = new FakeUploadTarget()
  response: unknown = null
  responseType = ''
  status = 200
  sentBody: XMLHttpRequestBodyInit | null = null
  private listeners = new Map<string, Array<() => void>>()

  constructor() {
    FakeXMLHttpRequest.instances.push(this)
  }

  open() {}

  addEventListener(type: string, listener: () => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }

  send(body: XMLHttpRequestBodyInit) {
    this.sentBody = body
  }

  abort() {
    this.emit('abort')
  }

  emit(type: string) {
    for (const listener of this.listeners.get(type) ?? []) {
      listener()
    }
  }
}

describe('ReceiveSharePage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    FakeXMLHttpRequest.instances = []
  })

  it('starts uploads after file selection and groups folder entries', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })
    const firstFile = createFile('a.txt', 'Photos/a.txt')
    const secondFile = createFile('b.txt', 'Photos/b.txt')
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [firstFile, secondFile],
    })
    await input.trigger('change')

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)
    expect(FakeXMLHttpRequest.instances[0].sentBody).toBeInstanceOf(FormData)
    expect(wrapper.text()).toContain('Photos')
    expect(wrapper.text()).toContain('2 items')
    expect(wrapper.find('.upload-dropzone__icon').exists()).toBe(true)
    expect(wrapper.find('.upload-dropzone').attributes('aria-label')).toBe('Drop files or folders here, or click to select files.')
    expect(wrapper.text()).not.toContain('Drop files or folders here')
    expect(wrapper.text()).not.toContain('Upload batch')
    expect(wrapper.text()).not.toContain('Ready')

    await wrapper.find('.folder-toggle').trigger('click')

    expect(wrapper.text()).toContain('a.txt')
    expect(wrapper.text()).toContain('b.txt')
  })
})

function createPage(): PublicSharePageModel {
  return {
    kind: 'receive',
    title: 'Upload to test',
    description: 'Upload files through Instant File Share.',
    canonicalUrl: 'https://example.test/r/token',
    siteName: 'Instant File Share',
    repositoryUrl: 'https://github.com/lugui1998/instant-file-share-3',
    receive: createReceive(),
  }
}

function createReceive(): PublicShareReceiveModel {
  return {
    targetName: 'drop',
    uploadUrl: 'https://example.test/r/token',
    remainingQuotaBytes: 1024,
    remainingQuotaLabel: '1 KB',
    expiresAtLabel: '6/30/2026 12:00 PM',
  }
}

function createFile(name: string, relativePath: string) {
  const file = new File(['content'], name, { type: 'text/plain' })
  Object.defineProperty(file, 'webkitRelativePath', {
    configurable: true,
    value: relativePath,
  })
  return file
}

import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ReceiveSharePage from './ReceiveSharePage.vue'
import type { PublicSharePageModel, PublicShareReceiveModel } from '../../types'

const uploadChunkSizeBytes = 16 * 1024 * 1024
const minimumUploadChunkSizeBytes = 1024 * 1024

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
  headers = new Map<string, string>()
  method = ''
  url = ''
  private listeners = new Map<string, Array<() => void>>()

  constructor() {
    FakeXMLHttpRequest.instances.push(this)
  }

  open(method: string, url: string) {
    this.method = method
    this.url = url
  }

  addEventListener(type: string, listener: () => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }

  setRequestHeader(name: string, value: string) {
    this.headers.set(name, value)
  }

  send(body?: XMLHttpRequestBodyInit | null) {
    this.sentBody = body ?? null
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

class FakeWebSocket {
  static instances: FakeWebSocket[] = []

  readonly url: string
  sent: Array<string | Blob | ArrayBufferLike | ArrayBufferView> = []
  private listeners = new Map<string, Array<(event: MessageEvent) => void>>()
  private openListeners: Array<() => void> = []
  private closeListeners: Array<() => void> = []
  private errorListeners: Array<() => void> = []

  constructor(url: string) {
    this.url = url
    FakeWebSocket.instances.push(this)
  }

  addEventListener(type: string, listener: EventListener) {
    if (type === 'open') {
      this.openListeners.push(listener as () => void)
      return
    }

    if (type === 'close') {
      this.closeListeners.push(listener as () => void)
      return
    }

    if (type === 'error') {
      this.errorListeners.push(listener as () => void)
      return
    }

    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener as (event: MessageEvent) => void])
  }

  removeEventListener(type: string, listener: EventListener) {
    if (type === 'message') {
      this.listeners.set(type, (this.listeners.get(type) ?? []).filter((candidate) => candidate !== listener))
    }
  }

  send(data: string | Blob | ArrayBufferLike | ArrayBufferView) {
    this.sent.push(data)
  }

  emitOpen() {
    for (const listener of this.openListeners) {
      listener()
    }
  }

  emitMessage(data: unknown) {
    for (const listener of this.listeners.get('message') ?? []) {
      listener(new MessageEvent('message', { data: JSON.stringify(data) }))
    }
  }

  close() {
    for (const listener of this.closeListeners) {
      listener()
    }
  }
}

class FakeCompressionStream {
  readonly readable: ReadableStream<Uint8Array>
  readonly writable: WritableStream<Uint8Array>

  constructor(format: CompressionFormat) {
    expect(format).toBe('gzip')
    const transform = new TransformStream<Uint8Array, Uint8Array>()
    this.readable = transform.readable
    this.writable = transform.writable
  }
}

class FakeResponse {
  constructor(_body: unknown) {
  }

  async blob() {
    return new Blob(['compressed'], { type: 'application/gzip' })
  }
}

describe('ReceiveSharePage', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
    FakeXMLHttpRequest.instances = []
    FakeWebSocket.instances = []
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

    expect(FakeXMLHttpRequest.instances).toHaveLength(2)
    expect(FakeXMLHttpRequest.instances[0].sentBody).toBeInstanceOf(FormData)
    expect(getFileSize(FakeXMLHttpRequest.instances[0])).toBe(firstFile.size.toString())
    expect(wrapper.text()).toContain('Photos')
    expect(wrapper.text()).toContain('2 items · 14 B')
    expect(wrapper.find('.upload-dropzone__icon').exists()).toBe(true)
    expect(wrapper.find('.upload-dropzone').attributes('aria-label')).toBe('Drop files or folders here, or click to select files.')
    expect(wrapper.find('.overall-progress-track').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('Drop files or folders here')
    expect(wrapper.text()).not.toContain('Upload batch')
    expect(wrapper.text()).not.toContain('Ready')

    await wrapper.find('.folder-toggle').trigger('click')

    expect(wrapper.text()).toContain('a.txt')
    expect(wrapper.text()).toContain('b.txt')
    expect(wrapper.text()).toContain('7 B')
  })

  it('accepts dropped files anywhere on the window', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })
    const droppedFile = createFile('window-drop.txt', 'window-drop.txt')

    window.dispatchEvent(createFileDragEvent('dragenter', [droppedFile]))
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.upload-dropzone').classes()).toContain('upload-dropzone--active')

    window.dispatchEvent(createFileDragEvent('drop', [droppedFile]))
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)
    expect(getFileName(FakeXMLHttpRequest.instances[0])).toBe('window-drop.txt')
  })

  it('ignores non-file drops on the window', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })

    window.dispatchEvent(createFileDragEvent('drop', [], ['text/plain']))
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(0)
  })

  it('omits overall progress for one file and hides stop after completion', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [createFile('receipt.txt', 'receipt.txt')],
    })
    await input.trigger('change')

    expect(wrapper.text()).not.toContain('Overall progress')
    expect(wrapper.text()).toContain('Stop')

    FakeXMLHttpRequest.instances[0].response = {
      uploadedCount: 1,
      failedCount: 0,
      remainingQuotaBytes: 1017,
      results: [
        {
          success: true,
          message: null,
          sizeBytes: 7,
        },
      ],
    }
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(wrapper.text()).not.toContain('Saved as')
    expect(wrapper.text()).not.toContain('Stop')
    expect(wrapper.text()).not.toContain('100%')
    expect(wrapper.find('.success-icon').exists()).toBe(true)
    expect(wrapper.find('.compact-track').exists()).toBe(false)
  })

  it('shows upload speed while a file is uploading', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const nowSpy = vi.spyOn(Date, 'now')
    nowSpy.mockReturnValue(1000)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [createFile('video.bin', 'video.bin', 'x'.repeat(8192))],
    })
    await input.trigger('change')

    nowSpy.mockReturnValue(3000)
    FakeXMLHttpRequest.instances[0].upload.emit('progress', new ProgressEvent('progress', {
      lengthComputable: true,
      loaded: 4096,
      total: 8192,
    }))
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain('2 KB/s')
  })

  it('sends cancel and ignores later host speed updates when stopped', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    vi.stubGlobal('WebSocket', FakeWebSocket)
    const fetchMock = vi.fn(() => Promise.resolve({ ok: true }))
    vi.stubGlobal('fetch', fetchMock)
    const nowSpy = vi.spyOn(Date, 'now')
    nowSpy.mockReturnValue(1000)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ uploadEventsUrl: '/r/token/events' }),
        receive: createReceive({ uploadEventsUrl: '/r/token/events' }),
      },
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [createFile('video.bin', 'video.bin', 'x'.repeat(8192))],
    })
    await input.trigger('change')

    const uploadId = getFormValue(FakeXMLHttpRequest.instances[0], 'uploadId')
    expect(typeof uploadId).toBe('string')
    const uploadIdValue = uploadId as string
    nowSpy.mockReturnValue(3000)
    FakeXMLHttpRequest.instances[0].upload.emit('progress', new ProgressEvent('progress', {
      lengthComputable: true,
      loaded: 4096,
      total: 8192,
    }))
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain('2 KB/s')

    const stopButton = wrapper.findAll('button').find((button) => button.text() === 'Stop')
    expect(stopButton).toBeDefined()
    await stopButton!.trigger('click')
    await vi.dynamicImportSettled()

    expect(fetchMock).toHaveBeenCalledWith(
      `https://example.test/r/token/cancel-upload?uploadId=${encodeURIComponent(uploadIdValue)}`,
      { method: 'POST', keepalive: true },
    )
    expect(wrapper.text()).toContain('Stopped')
    expect(wrapper.text()).not.toContain('2 KB/s')

    nowSpy.mockReturnValue(5000)
    FakeWebSocket.instances[0].emitMessage({
      uploadId: uploadIdValue,
      receivedBytes: 4096,
      totalBytes: 8192,
      state: 'InProgress',
      succeeded: false,
      error: null,
      recommendedChunkSizeBytes: 2 * 1024 * 1024,
    })
    await vi.advanceTimersByTimeAsync(250)

    expect(wrapper.text()).toContain('Stopped')
    expect(wrapper.text()).not.toContain('KB/s')
  })

  it('shows saving state after bytes are sent and before the server confirms completion', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const nowSpy = vi.spyOn(Date, 'now')
    nowSpy.mockReturnValue(1000)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [createFile('book.pdf', 'book.pdf', 'x'.repeat(4096))],
    })
    await input.trigger('change')

    nowSpy.mockReturnValue(2000)
    FakeXMLHttpRequest.instances[0].upload.emit('progress', new ProgressEvent('progress', {
      lengthComputable: true,
      loaded: 4096,
      total: 4096,
    }))
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).not.toContain('Saving...')
    expect(wrapper.text()).toContain('100%')
    expect(wrapper.text()).not.toContain('Stop')
    expect(wrapper.text()).not.toContain('4 KB/s')
    expect(wrapper.find('.success-icon').exists()).toBe(false)
    expect(wrapper.find('.progress-fill--client').exists()).toBe(true)

    FakeXMLHttpRequest.instances[0].response = createUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(wrapper.text()).not.toContain('Saved as')
    expect(wrapper.find('.success-icon').exists()).toBe(true)
  })

  it('uploads large files in chunks with chunk metadata', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })
    const largeFile = createSizedFile('large-video.bin', 'folder/large-video.bin', uploadChunkSizeBytes + 10)
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [largeFile],
    })
    await input.trigger('change')

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)
    expect(getFormValue(FakeXMLHttpRequest.instances[0], 'relativePaths')).toBe('folder/large-video.bin')
    expect(getFormValue(FakeXMLHttpRequest.instances[0], 'batchId')).toBeTruthy()
    expect(getFormValue(FakeXMLHttpRequest.instances[0], 'fileSizes')).toBe(largeFile.size.toString())
    expect(getFormValue(FakeXMLHttpRequest.instances[0], 'uploadId')).toBeTruthy()
    expect(getFormValue(FakeXMLHttpRequest.instances[0], 'chunkIndex')).toBe('0')
    expect(getFormValue(FakeXMLHttpRequest.instances[0], 'chunkCount')).toBe('2')
    expect(getFormValue(FakeXMLHttpRequest.instances[0], 'chunkStart')).toBe('0')
    expect(getFormValue(FakeXMLHttpRequest.instances[0], 'chunkSize')).toBe(uploadChunkSizeBytes.toString())
    expect(getUploadedFile(FakeXMLHttpRequest.instances[0])?.name).toBe('large-video.bin')
    expect(getUploadedFile(FakeXMLHttpRequest.instances[0])?.size).toBe(uploadChunkSizeBytes)

    FakeXMLHttpRequest.instances[0].response = createChunkUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(2)
    expect(wrapper.text()).toContain('100%')
    expect(wrapper.text()).not.toContain('Saved as folder/large-video.bin')
    expect(getFormValue(FakeXMLHttpRequest.instances[1], 'uploadId')).toBe(getFormValue(FakeXMLHttpRequest.instances[0], 'uploadId'))
    expect(getFormValue(FakeXMLHttpRequest.instances[1], 'chunkIndex')).toBe('1')
    expect(getFormValue(FakeXMLHttpRequest.instances[1], 'chunkCount')).toBe('2')
    expect(getFormValue(FakeXMLHttpRequest.instances[1], 'chunkStart')).toBe(uploadChunkSizeBytes.toString())
    expect(getFormValue(FakeXMLHttpRequest.instances[1], 'chunkSize')).toBe('10')
    expect(getUploadedFile(FakeXMLHttpRequest.instances[1])?.name).toBe('large-video.bin')
    expect(getUploadedFile(FakeXMLHttpRequest.instances[1])?.size).toBe(10)

    FakeXMLHttpRequest.instances[1].response = createUploadResponse()
    FakeXMLHttpRequest.instances[1].emit('load')
    await vi.dynamicImportSettled()
    await wrapper.find('.folder-toggle').trigger('click')

    expect(wrapper.text()).not.toContain('Saved as folder/large-video.bin')
    expect(wrapper.find('.success-icon').exists()).toBe(true)
  })

  it('stops chunked uploads after a server failure result', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })
    const largeFile = createSizedFile('broken.bin', 'broken.bin', uploadChunkSizeBytes + 10)
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [largeFile],
    })
    await input.trigger('change')

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)

    FakeXMLHttpRequest.instances[0].status = 409
    FakeXMLHttpRequest.instances[0].status = 409
    FakeXMLHttpRequest.instances[0].response = createFailedUploadResponse('The upload session was not found.')
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)
    expect(wrapper.text()).toContain('The upload session was not found.')
    expect(wrapper.text()).toContain('Restart')
  })

  it('uses a fresh upload ID for each retry attempt', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [createSizedFile('retry.bin', 'retry.bin', uploadChunkSizeBytes + 10)],
    })
    await input.trigger('change')

    const firstUploadId = getFormValue(FakeXMLHttpRequest.instances[0], 'uploadId')
    FakeXMLHttpRequest.instances[0].response = createFailedUploadResponse('The upload session was not found.')
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    const restartButton = wrapper.findAll('button').find((button) => button.text() === 'Restart')
    expect(restartButton).toBeDefined()
    await restartButton!.trigger('click')
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(2)
    expect(getFormValue(FakeXMLHttpRequest.instances[1], 'uploadId')).not.toBe(firstUploadId)
  })

  it('uploads large files as binary chunk requests when configured', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ uploadMode: 'BinaryChunks' }),
        receive: createReceive({ uploadMode: 'BinaryChunks' }),
      },
    })
    const largeFile = createSizedFile('large-video.bin', 'folder/large-video.bin', uploadChunkSizeBytes + 10)
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [largeFile],
    })
    await input.trigger('change')

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)
    expect(FakeXMLHttpRequest.instances[0].sentBody).toBeInstanceOf(Blob)
    expect((FakeXMLHttpRequest.instances[0].sentBody as Blob).size).toBe(uploadChunkSizeBytes)
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'Content-Type')).toBe('application/octet-stream')
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Relative-Path')).toBe(encodeURIComponent('folder/large-video.bin'))
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-File-Name')).toBe(encodeURIComponent('large-video.bin'))
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-File-Size')).toBe(largeFile.size.toString())
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Upload-Id')).toBeTruthy()
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Batch-Id')).toBeTruthy()
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Chunk-Index')).toBe('0')
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Chunk-Count')).toBe('2')
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Chunk-Start')).toBe('0')
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Chunk-Size')).toBe(uploadChunkSizeBytes.toString())

    FakeXMLHttpRequest.instances[0].response = createChunkUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(2)
    expect(FakeXMLHttpRequest.instances[1].sentBody).toBeInstanceOf(Blob)
    expect((FakeXMLHttpRequest.instances[1].sentBody as Blob).size).toBe(10)
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Upload-Id')).toBe(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Upload-Id'))
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Chunk-Index')).toBe('1')
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Chunk-Start')).toBe(uploadChunkSizeBytes.toString())
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Chunk-Size')).toBe('10')

    FakeXMLHttpRequest.instances[1].response = createUploadResponse()
    FakeXMLHttpRequest.instances[1].emit('load')
    await vi.dynamicImportSettled()
    await wrapper.find('.folder-toggle').trigger('click')

    expect(wrapper.text()).not.toContain('Saved as folder/large-video.bin')
    expect(wrapper.find('.success-icon').exists()).toBe(true)
  })

  it('uploads files as compressed streams when configured', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    vi.stubGlobal('CompressionStream', FakeCompressionStream)
    vi.stubGlobal('Response', FakeResponse)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ uploadMode: 'CompressedStream' }),
        receive: createReceive({ uploadMode: 'CompressedStream' }),
      },
    })
    const file = createFile('report.txt', 'folder/report.txt', 'compress me')
    Object.defineProperty(file, 'stream', {
      configurable: true,
      value: () => ({
        pipeThrough: () => ({}),
      }),
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [file],
    })
    await input.trigger('change')
    await waitForCondition(() => FakeXMLHttpRequest.instances.length === 1)

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)
    expect(FakeXMLHttpRequest.instances[0].sentBody).toBeInstanceOf(Blob)
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'Content-Type')).toBe('application/gzip')
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Relative-Path')).toBe(encodeURIComponent('folder/report.txt'))
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-File-Name')).toBe(encodeURIComponent('report.txt'))
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-File-Size')).toBe(file.size.toString())
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Upload-Id')).toBeTruthy()
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Batch-Id')).toBeTruthy()
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Chunk-Index')).toBeUndefined()

    FakeXMLHttpRequest.instances[0].response = createUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(wrapper.find('.success-icon').exists()).toBe(true)
  })

  it('uses the configured receive upload chunk size', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const customChunkSizeBytes = 2 * 1024 * 1024
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ uploadMode: 'BinaryChunks', uploadChunkSizeBytes: customChunkSizeBytes }),
        receive: createReceive({ uploadMode: 'BinaryChunks', uploadChunkSizeBytes: customChunkSizeBytes }),
      },
    })
    const largeFile = createSizedFile('custom.bin', 'custom.bin', customChunkSizeBytes + 5)
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [largeFile],
    })
    await input.trigger('change')

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)
    expect((FakeXMLHttpRequest.instances[0].sentBody as Blob).size).toBe(customChunkSizeBytes)
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Chunk-Size')).toBe(customChunkSizeBytes.toString())

    FakeXMLHttpRequest.instances[0].response = createChunkUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(2)
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Chunk-Start')).toBe(customChunkSizeBytes.toString())
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Chunk-Size')).toBe('5')

    FakeXMLHttpRequest.instances[1].response = createUploadResponse()
    FakeXMLHttpRequest.instances[1].emit('load')
    await vi.dynamicImportSettled()

    expect(wrapper.text()).not.toContain('Saved as custom.bin')
    expect(wrapper.find('.success-icon').exists()).toBe(true)
  })

  it('caps receive upload chunks at the configured max body size', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const customChunkSizeBytes = 8 * 1024 * 1024
    const maxBodySizeBytes = 3 * 1024 * 1024
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({
          uploadMode: 'BinaryChunks',
          uploadChunkSizeBytes: customChunkSizeBytes,
          uploadMaxBodySizeBytes: maxBodySizeBytes,
        }),
        receive: createReceive({
          uploadMode: 'BinaryChunks',
          uploadChunkSizeBytes: customChunkSizeBytes,
          uploadMaxBodySizeBytes: maxBodySizeBytes,
        }),
      },
    })
    const largeFile = createSizedFile('capped.bin', 'capped.bin', maxBodySizeBytes + 5)
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [largeFile],
    })
    await input.trigger('change')

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)
    expect((FakeXMLHttpRequest.instances[0].sentBody as Blob).size).toBe(maxBodySizeBytes)
    expect(getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Chunk-Size')).toBe(maxBodySizeBytes.toString())

    FakeXMLHttpRequest.instances[0].response = createChunkUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(2)
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Chunk-Start')).toBe(maxBodySizeBytes.toString())
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Chunk-Size')).toBe('5')

    FakeXMLHttpRequest.instances[1].response = createUploadResponse()
    FakeXMLHttpRequest.instances[1].emit('load')
    await vi.dynamicImportSettled()

    expect(wrapper.find('.success-icon').exists()).toBe(true)
  })

  it('uses host-recommended chunk sizes in adaptive binary mode', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    vi.stubGlobal('WebSocket', FakeWebSocket)
    const adaptiveChunkSizeBytes = 2 * 1024 * 1024
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ uploadEventsUrl: '/r/token/events', uploadMode: 'BinaryChunks', uploadChunkSizingMode: 'Auto' }),
        receive: createReceive({ uploadEventsUrl: '/r/token/events', uploadMode: 'BinaryChunks', uploadChunkSizingMode: 'Auto' }),
      },
    })
    const largeFile = createSizedFile('adaptive.bin', 'adaptive.bin', minimumUploadChunkSizeBytes + adaptiveChunkSizeBytes + 5)
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [largeFile],
    })
    await input.trigger('change')

    expect(FakeXMLHttpRequest.instances).toHaveLength(1)
    expect((FakeXMLHttpRequest.instances[0].sentBody as Blob).size).toBe(minimumUploadChunkSizeBytes)
    FakeWebSocket.instances[0].emitMessage({
      uploadId: getRequestHeader(FakeXMLHttpRequest.instances[0], 'X-IFS-Upload-Id'),
      receivedBytes: minimumUploadChunkSizeBytes,
      totalBytes: largeFile.size,
      state: 'InProgress',
      succeeded: false,
      error: null,
      recommendedChunkSizeBytes: adaptiveChunkSizeBytes,
    })

    FakeXMLHttpRequest.instances[0].response = createChunkUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(2)
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Chunk-Start')).toBe(minimumUploadChunkSizeBytes.toString())
    expect(getRequestHeader(FakeXMLHttpRequest.instances[1], 'X-IFS-Chunk-Size')).toBe(adaptiveChunkSizeBytes.toString())

    FakeXMLHttpRequest.instances[1].response = createChunkUploadResponse()
    FakeXMLHttpRequest.instances[1].emit('load')
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(3)
    expect(getRequestHeader(FakeXMLHttpRequest.instances[2], 'X-IFS-Chunk-Start')).toBe((minimumUploadChunkSizeBytes + adaptiveChunkSizeBytes).toString())
    expect(getRequestHeader(FakeXMLHttpRequest.instances[2], 'X-IFS-Chunk-Size')).toBe('5')

    FakeXMLHttpRequest.instances[2].response = createUploadResponse()
    FakeXMLHttpRequest.instances[2].emit('load')
    await vi.dynamicImportSettled()

    expect(wrapper.find('.success-icon').exists()).toBe(true)
  })

  it('uploads large files through the upload websocket when configured', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    vi.stubGlobal('WebSocket', FakeWebSocket)
    const customChunkSizeBytes = 2 * 1024 * 1024
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ uploadMode: 'WebSocket', uploadChunkSizeBytes: customChunkSizeBytes }),
        receive: createReceive({ uploadMode: 'WebSocket', uploadChunkSizeBytes: customChunkSizeBytes }),
      },
    })
    const largeFile = createSizedFile('socket.bin', 'socket.bin', customChunkSizeBytes + 7)
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [largeFile],
    })
    await input.trigger('change')

    expect(FakeWebSocket.instances).toHaveLength(1)
    expect(FakeWebSocket.instances[0].url).toBe('wss://example.test/r/token/upload-socket')

    FakeWebSocket.instances[0].emitOpen()
    await vi.dynamicImportSettled()

    expect(FakeWebSocket.instances[0].sent).toHaveLength(2)
    expect(JSON.parse(FakeWebSocket.instances[0].sent[0] as string)).toMatchObject({
      type: 'chunk',
      relativePath: 'socket.bin',
      chunkIndex: 0,
      chunkStart: 0,
      chunkSize: customChunkSizeBytes,
    })
    expect(FakeWebSocket.instances[0].sent[1]).toBeInstanceOf(Blob)
    expect((FakeWebSocket.instances[0].sent[1] as Blob).size).toBe(customChunkSizeBytes)

    FakeWebSocket.instances[0].emitMessage(createChunkUploadResponse())
    await vi.dynamicImportSettled()

    expect(FakeWebSocket.instances[0].sent).toHaveLength(4)
    expect(JSON.parse(FakeWebSocket.instances[0].sent[2] as string)).toMatchObject({
      type: 'chunk',
      chunkIndex: 1,
      chunkStart: customChunkSizeBytes,
      chunkSize: 7,
    })
    expect((FakeWebSocket.instances[0].sent[3] as Blob).size).toBe(7)

    FakeWebSocket.instances[0].emitMessage(createUploadResponse())
    await vi.dynamicImportSettled()

    expect(wrapper.find('.success-icon').exists()).toBe(true)
  })

  it('reports large-file progress from total uploaded bytes across chunks', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage(),
        receive: createReceive(),
      },
    })
    const largeFile = createSizedFile('archive.zip', 'archive.zip', uploadChunkSizeBytes * 2)
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [largeFile],
    })
    await input.trigger('change')

    FakeXMLHttpRequest.instances[0].upload.emit('progress', new ProgressEvent('progress', {
      lengthComputable: true,
      loaded: uploadChunkSizeBytes / 2,
      total: uploadChunkSizeBytes,
    }))
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain('25%')

    FakeXMLHttpRequest.instances[0].response = createChunkUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    FakeXMLHttpRequest.instances[1].upload.emit('progress', new ProgressEvent('progress', {
      lengthComputable: true,
      loaded: uploadChunkSizeBytes / 2,
      total: uploadChunkSizeBytes,
    }))
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain('75%')
  })

  it('shows host receive progress from upload websocket events', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    vi.stubGlobal('WebSocket', FakeWebSocket)
    vi.setSystemTime(1000)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ uploadEventsUrl: '/r/token/events' }),
        receive: createReceive({ uploadEventsUrl: '/r/token/events' }),
      },
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [createFile('report.pdf', 'report.pdf', 'x'.repeat(100))],
    })
    await input.trigger('change')

    expect(FakeWebSocket.instances[0].url).toBe('ws://localhost:3000/r/token/events')
    FakeXMLHttpRequest.instances[0].upload.emit('progress', new ProgressEvent('progress', {
      lengthComputable: true,
      loaded: 100,
      total: 100,
    }))
    FakeWebSocket.instances[0].emitMessage({
      uploadId: getFormValue(FakeXMLHttpRequest.instances[0], 'uploadId'),
      receivedBytes: 20,
      totalBytes: 100,
      state: 'InProgress',
      succeeded: false,
      error: null,
    })
    vi.setSystemTime(2000)
    FakeWebSocket.instances[0].emitMessage({
      uploadId: getFormValue(FakeXMLHttpRequest.instances[0], 'uploadId'),
      receivedBytes: 40,
      totalBytes: 100,
      state: 'InProgress',
      succeeded: false,
      error: null,
    })

    expect(wrapper.text()).not.toContain('40%')

    await vi.advanceTimersByTimeAsync(250)
    await wrapper.vm.$nextTick()

    const progressBar = wrapper.get('[role="progressbar"]')
    expect(progressBar.attributes('aria-label')).toBe('Upload progress')
    expect(progressBar.attributes('aria-valuenow')).toBe('40')
    expect(wrapper.text()).toContain('40%')
    expect(wrapper.text()).toContain('16 B/s')
    expect(progressBar.get('.progress-fill--client').attributes('style')).toContain('width: 100%')
    expect(progressBar.get('.progress-fill--receive').attributes('style')).toContain('width: 40%')
  })

  it('starts queued uploads up to the configured parallel limit', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ parallelUploadLimit: 2 }),
        receive: createReceive({ parallelUploadLimit: 2 }),
      },
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [
        createFile('one.txt', 'one.txt'),
        createFile('two.txt', 'two.txt'),
        createFile('three.txt', 'three.txt'),
      ],
    })
    await input.trigger('change')

    expect(FakeXMLHttpRequest.instances).toHaveLength(2)

    FakeXMLHttpRequest.instances[0].response = createUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    await vi.dynamicImportSettled()

    expect(FakeXMLHttpRequest.instances).toHaveLength(3)
  })

  it('treats zero parallel upload limit as uncapped', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ parallelUploadLimit: 0 }),
        receive: createReceive({ parallelUploadLimit: 0 }),
      },
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [
        createFile('one.txt', 'one.txt'),
        createFile('two.txt', 'two.txt'),
        createFile('three.txt', 'three.txt'),
      ],
    })
    await input.trigger('change')

    expect(FakeXMLHttpRequest.instances).toHaveLength(3)
  })

  it('keeps additions during active uploads in the current batch and starts a new batch after completion', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXMLHttpRequest)
    const wrapper = mount(ReceiveSharePage, {
      props: {
        page: createPage({ parallelUploadLimit: 2 }),
        receive: createReceive({ parallelUploadLimit: 2 }),
      },
    })
    const input = wrapper.find('input[type="file"]')

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [createFile('one.txt', 'one.txt')],
    })
    await input.trigger('change')

    const firstBatchId = getBatchId(FakeXMLHttpRequest.instances[0])

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [createFile('two.txt', 'two.txt')],
    })
    await input.trigger('change')

    expect(getBatchId(FakeXMLHttpRequest.instances[1])).toBe(firstBatchId)

    FakeXMLHttpRequest.instances[0].response = createUploadResponse()
    FakeXMLHttpRequest.instances[1].response = createUploadResponse()
    FakeXMLHttpRequest.instances[0].emit('load')
    FakeXMLHttpRequest.instances[1].emit('load')
    await vi.dynamicImportSettled()

    const completionRequest = FakeXMLHttpRequest.instances.find((request) => request.url.includes('ifs=batch-complete'))
    expect(completionRequest?.url).toContain(encodeURIComponent(firstBatchId))

    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [createFile('three.txt', 'three.txt')],
    })
    await input.trigger('change')

    const nextUpload = FakeXMLHttpRequest.instances.find((request) => getFileName(request) === 'three.txt')
    expect(nextUpload).toBeDefined()
    expect(getBatchId(nextUpload!)).not.toBe(firstBatchId)
  })
})

function createPage(receive: Partial<PublicShareReceiveModel> = {}): PublicSharePageModel {
  return {
    kind: 'receive',
    title: 'Upload to test',
    description: 'Upload files through Instant File Share.',
    canonicalUrl: 'https://example.test/r/token',
    siteName: 'Instant File Share',
    repositoryUrl: 'https://github.com/lugui1998/instant-file-share-3',
    receive: createReceive(receive),
  }
}

function createReceive(overrides: Partial<PublicShareReceiveModel> = {}): PublicShareReceiveModel {
  return {
    targetName: 'drop',
    uploadUrl: 'https://example.test/r/token',
    uploadSocketUrl: 'https://example.test/r/token/upload-socket',
    uploadEventsUrl: '',
    remainingQuotaBytes: 1024,
    remainingQuotaLabel: '1 KB',
    parallelUploadLimit: 2,
    uploadMode: 'MultipartChunks',
    uploadChunkSizingMode: 'Fixed',
    uploadChunkSizeBytes,
    uploadMaxBodySizeBytes: 95 * 1024 * 1024,
    uploadChunkTargetSeconds: 30,
    expiresAtLabel: '6/30/2026 12:00 PM',
    ...overrides,
  }
}

function createUploadResponse() {
  return {
    uploadedCount: 1,
    failedCount: 0,
    remainingQuotaBytes: 1017,
    results: [
      {
        success: true,
        message: null,
        sizeBytes: 7,
      },
    ],
  }
}

function createChunkUploadResponse() {
  return {
    uploadedCount: 0,
    failedCount: 0,
    remainingQuotaBytes: 1017,
    results: [],
  }
}

function createFailedUploadResponse(message: string) {
  return {
    uploadedCount: 0,
    failedCount: 1,
    remainingQuotaBytes: 1017,
    results: [
      {
        success: false,
        message,
        sizeBytes: 0,
      },
    ],
  }
}

async function waitForCondition(predicate: () => boolean) {
  for (let index = 0; index < 20; index += 1) {
    if (predicate()) {
      return
    }

    await new Promise((resolve) => setTimeout(resolve, 0))
  }
}

function getBatchId(request: FakeXMLHttpRequest) {
  return (request.sentBody as FormData).get('batchId') as string
}

function getFileName(request: FakeXMLHttpRequest) {
  return (request.sentBody as FormData | null)?.get('relativePaths')
}

function getFileSize(request: FakeXMLHttpRequest) {
  return (request.sentBody as FormData | null)?.get('fileSizes')
}

function getFormValue(request: FakeXMLHttpRequest, name: string) {
  return (request.sentBody as FormData | null)?.get(name)
}

function getUploadedFile(request: FakeXMLHttpRequest) {
  return (request.sentBody as FormData | null)?.get('files') as File | null
}

function getRequestHeader(request: FakeXMLHttpRequest, name: string) {
  return request.headers.get(name)
}

function createFileDragEvent(type: string, files: File[], types = ['Files']) {
  const event = new Event(type, { bubbles: true, cancelable: true }) as DragEvent
  Object.defineProperty(event, 'dataTransfer', {
    configurable: true,
    value: {
      types,
      files,
      items: [],
      dropEffect: 'none',
    },
  })
  return event
}

function createFile(name: string, relativePath: string, content = 'content') {
  const file = new File([content], name, { type: 'text/plain' })
  Object.defineProperty(file, 'webkitRelativePath', {
    configurable: true,
    value: relativePath,
  })
  return file
}

function createSizedFile(name: string, relativePath: string, size: number) {
  const file = new File([new Uint8Array(size)], name, { type: 'application/octet-stream' })
  Object.defineProperty(file, 'webkitRelativePath', {
    configurable: true,
    value: relativePath,
  })
  return file
}

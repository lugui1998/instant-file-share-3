import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import FileSharePage from './FileSharePage.vue'
import type { PublicShareFileModel, PublicSharePageModel } from '../../types'
import {
  createBrowserTransferKeyFragment,
  encryptBrowserTransferChunk,
  importBrowserTransferKey,
} from '../../crypto/browserTransferCrypto'

describe('FileSharePage', () => {
  afterEach(() => {
    window.location.hash = ''
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('does not fetch encrypted download metadata when the fragment key is missing', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile(),
      },
    })

    await wrapper.find('button').trigger('click')

    expect(fetchMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Missing download key')
  })

  it('decrypts an encrypted download with a URL-fragment key and never sends the key in requests', async () => {
    const keyBytes = new Uint8Array(32).fill(21)
    const key = await importBrowserTransferKey(keyBytes)
    const noncePrefix = new Uint8Array([1, 2, 3, 4])
    const encrypted = await encryptBrowserTransferChunk(
      key,
      new TextEncoder().encode('browser secret'),
      noncePrefix,
      0,
    )
    window.location.hash = createBrowserTransferKeyFragment(keyBytes, 'download')
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(jsonResponse({
        algorithm: 'AES-GCM',
        encryptedDownloadUrl: '/s/file-token?ifs=encrypted-download',
        fileName: 'secret.txt',
        contentType: 'text/plain',
        chunkIndex: 0,
        ivBase64Url: 'AQIDBAAAAAAAAAAA',
        keyDelivery: 'fragment',
      }))
      .mockResolvedValueOnce(arrayBufferResponse(encrypted.ciphertext.buffer))
    vi.stubGlobal('fetch', fetchMock)
    const objectUrlSpy = vi.fn(() => 'blob:download')
    const revokeSpy = vi.fn()
    vi.stubGlobal('URL', {
      createObjectURL: objectUrlSpy,
      revokeObjectURL: revokeSpy,
    })
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile(),
      },
    })

    await wrapper.find('button').trigger('click')
    await vi.waitFor(() => expect(wrapper.text()).toContain('decrypted in the browser'))

    expect(fetchMock).toHaveBeenCalledWith('/s/file-token?ifs=encrypted-download-plan', { cache: 'no-store' })
    expect(fetchMock).toHaveBeenCalledWith('/s/file-token?ifs=encrypted-download', { cache: 'no-store' })
    expect(fetchMock.mock.calls.map(([url]) => String(url)).every((url) => !url.includes('ifs-key'))).toBe(true)
    expect(objectUrlSpy).toHaveBeenCalledWith(expect.any(Blob))
    expect(clickSpy).toHaveBeenCalled()
    expect(revokeSpy).toHaveBeenCalledWith('blob:download')
  })

  it('offers the standard download action when encrypted decryption fails', async () => {
    const keyBytes = new Uint8Array(32).fill(21)
    window.location.hash = createBrowserTransferKeyFragment(keyBytes, 'download')
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(jsonResponse({
        algorithm: 'AES-GCM',
        encryptedDownloadUrl: '/s/file-token?ifs=encrypted-download',
        fileName: 'secret.txt',
        contentType: 'text/plain',
        chunkIndex: 0,
        ivBase64Url: 'AQIDBAAAAAAAAAAA',
        keyDelivery: 'fragment',
      }))
      .mockResolvedValueOnce(arrayBufferResponse(new Uint8Array([1, 2, 3]).buffer)))
    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: createFile(),
      },
    })

    await wrapper.find('button').trigger('click')
    await vi.waitFor(() => expect(wrapper.text()).toContain('Use the standard download action instead'))

    const fallback = wrapper.find('a.icon-text-button')
    expect(fallback.text()).toBe('Standard download')
    expect(fallback.attributes('href')).toBe('https://example.test/s/file-token')
  })

  it('does not advertise encrypted downloads without a manifest URL', () => {
    const file = createFile()
    const wrapper = mount(FileSharePage, {
      props: {
        page: createPage(),
        file: {
          ...file,
          encryptionExperiment: {
            ...file.encryptionExperiment!,
            downloadManifestUrl: null,
            encryptedDownloadUrl: null,
          },
        },
      },
    })

    expect(wrapper.text()).not.toContain('Encryption experiment')
    expect(wrapper.find('button').exists()).toBe(false)
  })
})

function createPage(): PublicSharePageModel {
  return {
    kind: 'file',
    title: 'secret.txt',
    description: 'Download secret.txt.',
    canonicalUrl: 'https://example.test/s/file-token',
    siteName: 'Instant File Share',
    repositoryUrl: 'https://github.com/lugui1998/instant-file-share-3',
    primaryActionLabel: 'Download file',
    primaryActionUrl: 'https://example.test/s/file-token',
  }
}

function createFile(): PublicShareFileModel {
  return {
    fileName: 'secret.txt',
    displaySize: '14 B',
    preferInline: false,
    actionVerb: 'Download',
    actionLabel: 'Open this link to download secret.txt.',
    encryptionExperiment: {
      downloadManifestUrl: '/s/file-token?ifs=encrypted-download-plan',
      encryptedDownloadUrl: '/s/file-token?ifs=encrypted-download',
      fragmentKeyParameter: 'ifs-key',
      algorithm: 'AES-GCM',
      ivStrategy: 'test',
      keyDelivery: 'fragment',
      receiveUploadModes: ['store-encrypted'],
    },
  }
}

function jsonResponse(body: unknown) {
  return {
    ok: true,
    status: 200,
    json: async () => body,
  }
}

function arrayBufferResponse(buffer: ArrayBufferLike) {
  return {
    ok: true,
    status: 200,
    arrayBuffer: async () => buffer,
  }
}

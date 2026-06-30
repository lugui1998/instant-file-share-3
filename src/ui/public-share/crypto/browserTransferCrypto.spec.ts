import { describe, expect, it } from 'vitest'
import {
  createBrowserTransferIv,
  createBrowserTransferKeyFragment,
  createBrowserTransferNoncePrefix,
  decryptBrowserTransferChunk,
  encryptBrowserTransferChunk,
  getBrowserTransferNoncePrefixLength,
  importBrowserTransferKey,
  parseBrowserTransferKeyFragment,
} from './browserTransferCrypto'

describe('browserTransferCrypto', () => {
  it('round-trips AES-GCM chunks with deterministic per-chunk IVs', async () => {
    const keyBytes = new Uint8Array(32).fill(7)
    const key = await importBrowserTransferKey(keyBytes)
    const noncePrefix = new Uint8Array([1, 2, 3, 4])
    const plaintext = new TextEncoder().encode('secret browser transfer')

    const encrypted = await encryptBrowserTransferChunk(key, plaintext, noncePrefix, 3)
    const decrypted = await decryptBrowserTransferChunk(key, encrypted)

    expect(new TextDecoder().decode(decrypted)).toBe('secret browser transfer')
    expect([...encrypted.iv]).toEqual([...createBrowserTransferIv(noncePrefix, 3)])
  })

  it('rejects tampered ciphertext with AES-GCM authentication failure', async () => {
    const key = await importBrowserTransferKey(new Uint8Array(32).fill(11))
    const noncePrefix = new Uint8Array([5, 6, 7, 8])
    const encrypted = await encryptBrowserTransferChunk(key, new TextEncoder().encode('authentic'), noncePrefix, 0)

    encrypted.ciphertext[0] ^= 0xff

    await expect(decryptBrowserTransferChunk(key, encrypted)).rejects.toThrow()
  })

  it('derives unique IVs per chunk and rejects invalid IV inputs', () => {
    const noncePrefix = new Uint8Array([9, 10, 11, 12])

    expect(createBrowserTransferIv(noncePrefix, 0)).not.toEqual(createBrowserTransferIv(noncePrefix, 1))
    expect(() => createBrowserTransferIv(noncePrefix, -1)).toThrow('Chunk index')
    expect(() => createBrowserTransferIv(new Uint8Array([1, 2, 3]), 0)).toThrow('nonce prefix')
  })

  it('creates random nonce prefixes for separate transfer keys', () => {
    const first = createBrowserTransferNoncePrefix()
    const second = createBrowserTransferNoncePrefix()

    expect(first).toHaveLength(getBrowserTransferNoncePrefixLength())
    expect(second).toHaveLength(getBrowserTransferNoncePrefixLength())
    expect(first).not.toEqual(second)
  })

  it('parses URL-fragment key material without accepting query-only keys', () => {
    const keyBytes = new Uint8Array(32).fill(3)
    const fragment = createBrowserTransferKeyFragment(keyBytes, 'download')
    const parsed = parseBrowserTransferKeyFragment(`#${fragment}`)

    expect(parsed?.version).toBe('aes-gcm-v1')
    expect(parsed?.mode).toBe('download')
    expect(parsed?.keyBytes).toEqual(keyBytes)
    expect(parseBrowserTransferKeyFragment('?ifs-crypto=aes-gcm-v1&ifs-key=not-a-fragment')).toBeNull()
    expect(parseBrowserTransferKeyFragment('#ifs-crypto=aes-gcm-v1')).toBeNull()
  })
})

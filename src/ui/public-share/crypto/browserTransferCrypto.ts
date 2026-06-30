export type BrowserTransferEncryptionMode = 'download' | 'upload'

export type BrowserTransferKeyFragment = {
  version: 'aes-gcm-v1'
  keyBytes: Uint8Array
  mode?: BrowserTransferEncryptionMode
}

export type BrowserTransferEncryptedChunk = {
  iv: Uint8Array
  ciphertext: Uint8Array
}

const aesGcmKeyLengthBits = 256
const aesGcmIvLengthBytes = 12
const chunkCounterBytes = 8
const noncePrefixBytes = aesGcmIvLengthBytes - chunkCounterBytes
const fragmentKeyName = 'ifs-key'
const fragmentVersionName = 'ifs-crypto'
const fragmentModeName = 'ifs-mode'
export const maxBrowserTransferBufferedBytes = 64 * 1024 * 1024

export function getBrowserTransferNoncePrefixLength() {
  return noncePrefixBytes
}

export async function generateBrowserTransferKey(mode?: BrowserTransferEncryptionMode) {
  const keyBytes = new Uint8Array(aesGcmKeyLengthBits / 8)
  globalThis.crypto.getRandomValues(keyBytes)
  const key = await importBrowserTransferKey(keyBytes)

  return {
    key,
    keyBytes,
    fragment: createBrowserTransferKeyFragment(keyBytes, mode),
  }
}

export function createBrowserTransferNoncePrefix() {
  const noncePrefix = new Uint8Array(noncePrefixBytes)
  globalThis.crypto.getRandomValues(noncePrefix)
  return noncePrefix
}

export function createBrowserTransferIv(noncePrefix: Uint8Array, chunkIndex: number) {
  if (noncePrefix.byteLength !== noncePrefixBytes) {
    throw new Error(`AES-GCM nonce prefix must be ${noncePrefixBytes} bytes.`)
  }

  if (!Number.isSafeInteger(chunkIndex) || chunkIndex < 0) {
    throw new Error('Chunk index must be a non-negative safe integer.')
  }

  const iv = new Uint8Array(aesGcmIvLengthBytes)
  iv.set(noncePrefix, 0)
  const view = new DataView(iv.buffer, iv.byteOffset + noncePrefixBytes, chunkCounterBytes)
  view.setBigUint64(0, BigInt(chunkIndex), false)
  if (iv.every((byte) => byte === 0)) {
    throw new Error('AES-GCM IV must not be all zero.')
  }
  return iv
}

export async function importBrowserTransferKey(keyBytes: Uint8Array) {
  if (keyBytes.byteLength !== aesGcmKeyLengthBits / 8) {
    throw new Error('AES-GCM transfer keys must be 256-bit.')
  }

  return globalThis.crypto.subtle.importKey(
    'raw',
    bytesToArrayBuffer(keyBytes),
    { name: 'AES-GCM', length: aesGcmKeyLengthBits },
    false,
    ['encrypt', 'decrypt'],
  )
}

export async function encryptBrowserTransferChunk(
  key: CryptoKey,
  plaintext: BufferSource,
  noncePrefix: Uint8Array,
  chunkIndex: number,
): Promise<BrowserTransferEncryptedChunk> {
  const iv = createBrowserTransferIv(noncePrefix, chunkIndex)
  const ciphertext = await globalThis.crypto.subtle.encrypt({ name: 'AES-GCM', iv: bytesToArrayBuffer(iv) }, key, plaintext)
  return {
    iv,
    ciphertext: new Uint8Array(ciphertext),
  }
}

export async function decryptBrowserTransferChunk(
  key: CryptoKey,
  chunk: BrowserTransferEncryptedChunk,
) {
  const plaintext = await globalThis.crypto.subtle.decrypt(
    { name: 'AES-GCM', iv: bytesToArrayBuffer(chunk.iv) },
    key,
    bytesToArrayBuffer(chunk.ciphertext),
  )
  return new Uint8Array(plaintext)
}

export function bytesToArrayBuffer(bytes: Uint8Array) {
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer
}

export function createBrowserTransferKeyFragment(
  keyBytes: Uint8Array,
  mode?: BrowserTransferEncryptionMode,
) {
  const params = new URLSearchParams()
  params.set(fragmentVersionName, 'aes-gcm-v1')
  params.set(fragmentKeyName, base64UrlEncode(keyBytes))
  if (mode) {
    params.set(fragmentModeName, mode)
  }
  return params.toString()
}

export function parseBrowserTransferKeyFragment(hash: string): BrowserTransferKeyFragment | null {
  const normalizedHash = hash.startsWith('#') ? hash.slice(1) : hash
  if (!normalizedHash) {
    return null
  }

  const params = new URLSearchParams(normalizedHash)
  if (params.get(fragmentVersionName) !== 'aes-gcm-v1') {
    return null
  }

  const keyValue = params.get(fragmentKeyName)
  if (!keyValue) {
    return null
  }

  const keyBytes = base64UrlDecode(keyValue)
  if (keyBytes.byteLength !== aesGcmKeyLengthBits / 8) {
    return null
  }

  const modeValue = params.get(fragmentModeName)
  const mode = modeValue === 'download' || modeValue === 'upload' ? modeValue : undefined
  return {
    version: 'aes-gcm-v1',
    keyBytes,
    mode,
  }
}

export function base64UrlEncode(bytes: Uint8Array) {
  let binary = ''
  for (const byte of bytes) {
    binary += String.fromCharCode(byte)
  }

  return btoa(binary)
    .replaceAll('+', '-')
    .replaceAll('/', '_')
    .replaceAll('=', '')
}

export function base64UrlDecode(value: string) {
  const normalized = value.replaceAll('-', '+').replaceAll('_', '/')
  const padded = normalized.padEnd(normalized.length + ((4 - normalized.length % 4) % 4), '=')
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index)
  }
  return bytes
}

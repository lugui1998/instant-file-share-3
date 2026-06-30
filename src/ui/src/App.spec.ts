import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App.vue'
import type { AppSettings } from './agentBridge'

const { saveSettingsMock } = vi.hoisted(() => ({
  saveSettingsMock: vi.fn(),
}))

vi.mock('./agentBridge', () => ({
  agentBridge: {
    checkManagedTunnelAvailability: vi.fn(),
    clearTransferHistory: vi.fn(),
    connectRuntime: vi.fn(() => () => {}),
    createManagedTunnel: vi.fn(),
    createShare: vi.fn(),
    getAgentLogs: vi.fn(async () => ''),
    getAppVersion: vi.fn(async () => '0.0.0-test'),
    getCloudflaredStatus: vi.fn(async () => null),
    getCloudflareLogs: vi.fn(async () => ''),
    getManagedCloudflareStatus: vi.fn(async () => ({ loggedIn: false, domains: [] })),
    getRuntime: vi.fn(async () => ({
      shares: [],
      transfers: [],
      settings: createSettings(),
      cloudflared: {},
    })),
    getTransfers: vi.fn(async () => []),
    installCloudflared: vi.fn(),
    logoutCloudflare: vi.fn(),
    pickCloudflaredExecutable: vi.fn(),
    removeTransfer: vi.fn(),
    revokeShare: vi.fn(),
    saveSettings: saveSettingsMock,
    showShareInExplorer: vi.fn(),
    startCloudflareLogin: vi.fn(),
    updateCloudflared: vi.fn(),
  },
}))

describe('App settings normalization', () => {
  afterEach(() => {
    vi.useRealTimers()
    saveSettingsMock.mockClear()
  })

  it('preserves auto receive mode and clamps the auto probing threshold when saving', async () => {
    vi.useFakeTimers()
    const wrapper = mount(App)
    await vi.dynamicImportSettled()

    await wrapper.findAll('button').find((button) => button.text() === 'Settings')!.trigger('click')
    await wrapper.find('#receive-upload-auto-probe-chunks').setValue('0')
    await vi.advanceTimersByTimeAsync(450)
    await vi.dynamicImportSettled()

    expect(saveSettingsMock).toHaveBeenCalled()
    expect(saveSettingsMock.mock.lastCall?.[0]).toMatchObject({
      receiveUploadMode: 'Auto',
      receiveUploadAutoProbeChunkCount: 1,
    })
  })
})

function createSettings(): AppSettings {
  return {
    defaultPublishMode: 'QuickTunnel',
    publicTokenLength: 11,
    defaultExpiryValue: 24,
    defaultExpiryUnit: 'Hours',
    defaultMaxUses: null,
    defaultReceiveExpiryValue: 24,
    defaultReceiveExpiryUnit: 'Hours',
    defaultReceiveMaxTotalBytes: 10 * 1024 * 1024 * 1024,
    receiveParallelUploadLimit: 4,
    receiveUploadMode: 'Auto',
    receiveUploadChunkSizingMode: 'Fixed',
    receiveUploadChunkSizeBytes: 16 * 1024 * 1024,
    receiveUploadMaxBodySizeBytes: 95 * 1024 * 1024,
    receiveUploadChunkTargetSeconds: 30,
    receiveUploadAutoProbeChunkCount: 4,
    browserManagedDownloadsEnabled: true,
    browserManagedDownloadMaxMemoryBytes: 512 * 1024 * 1024,
    browserManagedDownloadMaxParallelChunks: 4,
    browserManagedCompressionMode: 'Auto',
    browserTransferEncryptionPolicy: 'HttpOnly',
    browserTransferDiagnosticsEnabled: false,
    folderBrowsePageTitle: 'Browse files',
    receivePageTitle: 'Upload files',
    friendlyUrlsEnabled: true,
    sendMetadataToCrawlers: true,
    openImagesInBrowser: true,
    openVideosInBrowser: true,
    openPdfInBrowser: true,
    fileChangeBehavior: 'Strict',
    keepAwakeWhileTransferring: true,
    bandwidthLimitBytesPerSecond: null,
    cloudflaredPathOverride: null,
    startOnLogin: true,
    openDashboardOnStart: false,
    manualBindAddress: '0.0.0.0',
    manualPublicPort: 46431,
    manualBaseUrl: null,
    localApiPort: 46430,
    showLogs: false,
    addFileContextMenuButton: true,
    addFolderZipContextMenuButton: true,
    addFolderBrowseContextMenuButton: true,
    addFolderReceiveContextMenuButton: true,
    receiveNotificationsEnabled: true,
    folderShareCapabilityPolicy: 'Exclusive',
    folderZipCompressionLevel: 'Optimal',
    historyRetentionValue: 3,
    historyRetentionUnit: 'Months',
    historyItemsPerPage: 25,
    sharesItemsPerPage: 25,
  }
}

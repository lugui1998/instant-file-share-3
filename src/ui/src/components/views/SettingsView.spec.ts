import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { AppSettings } from '../../agentBridge'
import SettingsView from './SettingsView.vue'

function createSettingsDraft(): AppSettings {
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
    receiveUploadCompressionEnabled: true,
    browserManagedDownloadsEnabled: true,
    browserManagedDownloadMaxMemoryBytes: 64 * 1024 * 1024,
    browserManagedDownloadMaxParallelChunks: 4,
    browserManagedCompressionMode: 'Auto',
    browserDownloadEncryptionPolicy: 'HttpOnly',
    receiveUploadEncryptionPolicy: 'HttpOnly',
    browserTransferDiagnosticsEnabled: false,
    receiveTransferDiagnosticsEnabled: false,
    folderBrowsePageTitle: '',
    receivePageTitle: '',
    friendlyUrlsEnabled: true,
    sendMetadataToCrawlers: true,
    openImagesInBrowser: true,
    openVideosInBrowser: true,
    openPdfInBrowser: true,
    fileChangeBehavior: 'Lenient',
    keepAwakeWhileTransferring: true,
    bandwidthLimitBytesPerSecond: null,
    cloudflaredPathOverride: null,
    startOnLogin: false,
    openDashboardOnStart: true,
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
    historyRetentionUnit: 'Days',
    historyItemsPerPage: 25,
    sharesItemsPerPage: 25,
  }
}

describe('SettingsView', () => {
  it('keeps Cloudflare after Receiving and Debug as the final settings card', () => {
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft: createSettingsDraft(),
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: {
          installed: true,
          executablePath: 'C:\\Tools\\cloudflared.exe',
          installedVersion: '2026.1.0',
          latestVersion: '2026.1.0',
          updateAvailable: false,
          ownership: 'Path',
          loggedIn: false,
          loginMessage: 'Not logged in',
        },
        clearingTransferHistory: false,
        managedStatus: {
          loggedIn: false,
          message: 'Not logged in',
          domains: [],
        },
        managedAvailability: null,
        saveMessage: '',
      },
    })

    const cardEyebrows = wrapper
      .findAll('.setting-card > .eyebrow')
      .map((eyebrow) => eyebrow.text())

    expect(cardEyebrows.slice(-4)).toEqual(['Browser Managed Transfers', 'Receiving', 'Cloudflare', 'Debug'])
  })

  it('shows the receive parallel upload limit setting', () => {
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft: createSettingsDraft(),
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: null,
        managedAvailability: null,
        saveMessage: '',
      },
    })

    const input = wrapper.find('#receive-parallel-upload-limit')

    expect(wrapper.text()).toContain('Parallel uploads')
    expect(input.exists()).toBe(true)
    expect(input.attributes('min')).toBe('0')
    expect(input.attributes('max')).toBeUndefined()
    expect(wrapper.text()).toContain('Use 0 for no limit.')
  })

  it('shows the receive upload mode setting', () => {
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft: createSettingsDraft(),
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: null,
        managedAvailability: null,
        saveMessage: '',
      },
    })

    const select = wrapper.find('#receive-upload-mode')

    expect(wrapper.text()).toContain('Upload mode')
    expect(select.exists()).toBe(true)
    expect(select.findAll('option').map((option) => option.attributes('value'))).toEqual([
      'Auto',
      'MultipartChunks',
      'BinaryChunks',
      'WebSocket',
    ])
    expect(wrapper.find('#receive-upload-compression-enabled').exists()).toBe(true)
    expect(wrapper.text()).toContain('Upload compression')
  })

  it('groups browser-managed download and upload settings together', () => {
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft: createSettingsDraft(),
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: null,
        managedAvailability: null,
        saveMessage: '',
      },
    })

    const browserManagedTransfersCard = wrapper
      .findAll('.setting-card')
      .find((card) => card.find('.eyebrow').text() === 'Browser Managed Transfers')
    const receivingCard = wrapper
      .findAll('.setting-card')
      .find((card) => card.find('.eyebrow').text() === 'Receiving')

    expect(browserManagedTransfersCard?.find('#browser-managed-downloads-enabled').exists()).toBe(true)
    expect(browserManagedTransfersCard?.find('#browser-managed-download-memory').exists()).toBe(true)
    expect(browserManagedTransfersCard?.text()).toContain('Default: 64 MB.')
    expect(browserManagedTransfersCard?.find('#browser-managed-download-parallel-chunks').exists()).toBe(true)
    expect(browserManagedTransfersCard?.find('#browser-managed-compression-mode').exists()).toBe(true)
    expect(browserManagedTransfersCard?.find('#browser-download-encryption-policy').exists()).toBe(true)
    expect(browserManagedTransfersCard?.find('#receive-upload-mode').exists()).toBe(true)
    expect(browserManagedTransfersCard?.find('#receive-upload-compression-enabled').exists()).toBe(true)
    expect(browserManagedTransfersCard?.find('#receive-upload-encryption-policy').exists()).toBe(true)
    expect(browserManagedTransfersCard?.find('#receive-upload-chunk-sizing-mode').exists()).toBe(true)
    expect(browserManagedTransfersCard?.find('#receive-upload-max-body-size').exists()).toBe(true)

    expect(receivingCard?.find('#receive-upload-mode').exists()).toBe(false)
    expect(receivingCard?.find('#receive-upload-compression-enabled').exists()).toBe(false)
    expect(receivingCard?.find('#receive-upload-encryption-policy').exists()).toBe(false)
    expect(receivingCard?.find('#browser-managed-downloads-enabled').exists()).toBe(false)
    expect(receivingCard?.find('#browser-download-encryption-policy').exists()).toBe(false)
  })

  it('keeps public transfer diagnostics toggles in Debug', () => {
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft: createSettingsDraft(),
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: null,
        managedAvailability: null,
        saveMessage: '',
      },
    })

    const debugCard = wrapper
      .findAll('.setting-card')
      .find((card) => card.find('.eyebrow').text() === 'Debug')
    const receivingCard = wrapper
      .findAll('.setting-card')
      .find((card) => card.find('.eyebrow').text() === 'Receiving')

    expect(debugCard?.find('#browser-transfer-diagnostics-enabled').exists()).toBe(true)
    expect(debugCard?.find('#receive-transfer-diagnostics-enabled').exists()).toBe(true)
    expect(receivingCard?.find('#browser-transfer-diagnostics-enabled').exists()).toBe(false)
    expect(receivingCard?.find('#receive-transfer-diagnostics-enabled').exists()).toBe(false)
  })

  it('shows the auto probing threshold only for auto receive uploads', async () => {
    const settingsDraft = createSettingsDraft()
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft,
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: null,
        managedAvailability: null,
        saveMessage: '',
      },
    })

    const input = wrapper.find('#receive-upload-auto-probe-chunks')
    expect(input.exists()).toBe(true)
    expect(input.element).toHaveProperty('value', '4')
    expect(input.attributes('min')).toBe('1')
    expect(wrapper.text()).toContain('Default: 4 chunks')

    await wrapper.find('#receive-upload-mode').setValue('BinaryChunks')

    expect(wrapper.find('#receive-upload-auto-probe-chunks').exists()).toBe(false)
  })

  it('shows fixed and automatic packet sizing settings', async () => {
    const settingsDraft = createSettingsDraft()
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft,
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: null,
        managedAvailability: null,
        saveMessage: '',
      },
    })

    const sizingSelect = wrapper.find('#receive-upload-chunk-sizing-mode')

    expect(wrapper.text()).toContain('Packet sizing')
    expect(sizingSelect.findAll('option').map((option) => option.attributes('value'))).toEqual(['Fixed', 'Auto'])
    expect(wrapper.find('#receive-upload-chunk-size').exists()).toBe(true)
    expect(wrapper.find('#receive-upload-chunk-target-seconds').exists()).toBe(false)

    await sizingSelect.setValue('Auto')

    expect(settingsDraft.receiveUploadChunkSizingMode).toBe('Auto')
    expect(wrapper.find('#receive-upload-chunk-size').exists()).toBe(false)
    const targetInput = wrapper.find('#receive-upload-chunk-target-seconds')
    expect(targetInput.exists()).toBe(true)
    expect(targetInput.attributes('min')).toBe('1')
    expect(targetInput.attributes('max')).toBeUndefined()
    expect(wrapper.text()).toContain('Minimum: 1 second')
  })

  it('warns when Cloudflare auto target request time is above 120 seconds', () => {
    const settingsDraft = createSettingsDraft()
    settingsDraft.defaultPublishMode = 'QuickTunnel'
    settingsDraft.receiveUploadChunkSizingMode = 'Auto'
    settingsDraft.receiveUploadChunkTargetSeconds = 180
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft,
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: null,
        managedAvailability: null,
        saveMessage: '',
      },
    })

    expect(wrapper.text()).toContain('Cloudflare can time out proxied requests after 120 seconds')
  })

  it('shows the receive upload chunk size setting in megabytes', () => {
    const settingsDraft = createSettingsDraft()
    settingsDraft.receiveUploadChunkSizeBytes = 8 * 1024 * 1024
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft,
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: null,
        managedAvailability: null,
        saveMessage: '',
      },
    })

    const input = wrapper.find('#receive-upload-chunk-size')

    expect(wrapper.text()).toContain('Packet size')
    expect(input.exists()).toBe(true)
    expect(input.element).toHaveProperty('value', '8')
    expect(input.attributes('min')).toBe('1')
    expect(wrapper.text()).toContain('Default: 16 MB')
  })

  it('shows the receive upload max body size setting in megabytes', () => {
    const settingsDraft = createSettingsDraft()
    settingsDraft.receiveUploadMaxBodySizeBytes = 90 * 1024 * 1024
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft,
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: null,
        managedAvailability: null,
        saveMessage: '',
      },
    })

    const input = wrapper.find('#receive-upload-max-body-size')

    expect(wrapper.text()).toContain('Max request body size')
    expect(input.exists()).toBe(true)
    expect(input.element).toHaveProperty('value', '90')
    expect(input.attributes('min')).toBe('1')
    expect(wrapper.text()).toContain('Cloudflare Free/Pro limit: 100 MB')
  })

  it('warns when Cloudflare Free plan max body size is above the plan limit', () => {
    const settingsDraft = createSettingsDraft()
    settingsDraft.defaultPublishMode = 'ManagedCloudflare'
    settingsDraft.receiveUploadMaxBodySizeBytes = 150 * 1024 * 1024
    const wrapper = mount(SettingsView, {
      props: {
        settingsDraft,
        bandwidthValue: null,
        bandwidthUnit: 'MB/s',
        selectedDomain: '',
        managedSubdomain: 'share',
        cloudflaredStatus: null,
        clearingTransferHistory: false,
        managedStatus: {
          loggedIn: true,
          message: 'Logged in.',
          zonePlanLegacyId: 'free',
        },
        managedAvailability: null,
        saveMessage: '',
      },
    })

    expect(wrapper.text()).toContain('Requests over 100 MB can fail with 413')
  })
})

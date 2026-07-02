import { mount, type VueWrapper } from '@vue/test-utils'
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

function mountSettingsView(settingsDraft = createSettingsDraft()) {
  return mount(SettingsView, {
    props: {
      settingsDraft,
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
}

async function openSettingsSection(wrapper: VueWrapper, label: string) {
  const sectionButton = wrapper
    .findAll('.settings-nav-button')
    .find((button) => button.text() === label)

  expect(sectionButton).toBeTruthy()
  await sectionButton!.trigger('click')
}

function activePanelTitle(wrapper: VueWrapper) {
  return wrapper.find('.settings-panel-header h2').text()
}

describe('SettingsView', () => {
  it('starts on General and exposes the settings category navigation', () => {
    const wrapper = mountSettingsView()

    const sectionLabels = wrapper.findAll('.settings-nav-button').map((button) => button.text())
    const selectedSection = wrapper.find('.settings-nav-button.active')

    expect(sectionLabels).toEqual([
      'General',
      'Connection',
      'Sending',
      'Receiving',
      'Transfer Engine',
      'Windows Integration',
      'History',
      'Diagnostics',
    ])
    expect(selectedSection.text()).toBe('General')
    expect(selectedSection.attributes('aria-selected')).toBe('true')
    expect(activePanelTitle(wrapper)).toBe('General')
    expect(wrapper.find('#keep-awake').exists()).toBe(true)
    expect(wrapper.find('#local-api-port').exists()).toBe(true)
    expect(wrapper.find('#default-publish-mode').exists()).toBe(false)
  })

  it('groups publishing, manual listener, and Cloudflare settings under Connection', async () => {
    const wrapper = mountSettingsView()

    await openSettingsSection(wrapper, 'Connection')

    expect(activePanelTitle(wrapper)).toBe('Connection')
    expect(wrapper.find('#default-publish-mode').exists()).toBe(true)
    expect(wrapper.find('#public-token-length').exists()).toBe(true)
    expect(wrapper.find('#bandwidth-limit').exists()).toBe(true)
    expect(wrapper.find('#manual-bind-address').exists()).toBe(true)
    expect(wrapper.find('#manual-public-port').exists()).toBe(true)
    expect(wrapper.find('#managed-domain').exists()).toBe(true)
    expect(wrapper.find('#cloudflared-path').exists()).toBe(true)
    expect(wrapper.find('#receive-upload-mode').exists()).toBe(false)
  })

  it('groups link, folder, and public display defaults under Sending', async () => {
    const wrapper = mountSettingsView()

    await openSettingsSection(wrapper, 'Sending')

    expect(activePanelTitle(wrapper)).toBe('Sending')
    expect(wrapper.find('#default-expiry-value').exists()).toBe(true)
    expect(wrapper.find('#default-max-uses').exists()).toBe(true)
    expect(wrapper.find('#file-change-behavior').exists()).toBe(true)
    expect(wrapper.find('#folder-browse-page-title').exists()).toBe(true)
    expect(wrapper.find('#folder-share-capability-policy').exists()).toBe(true)
    expect(wrapper.find('#folder-zip-compression-level').exists()).toBe(true)
    expect(wrapper.find('#send-metadata-to-crawlers').exists()).toBe(true)
    expect(wrapper.find('#open-pdf-in-browser').exists()).toBe(true)
    expect(wrapper.find('#file-context-button').exists()).toBe(false)
  })

  it('keeps receive-link defaults separate from upload transfer tuning', async () => {
    const wrapper = mountSettingsView()

    await openSettingsSection(wrapper, 'Receiving')

    const input = wrapper.find('#receive-parallel-upload-limit')

    expect(activePanelTitle(wrapper)).toBe('Receiving')
    expect(wrapper.find('#receive-page-title').exists()).toBe(true)
    expect(wrapper.find('#default-receive-expiry-value').exists()).toBe(true)
    expect(wrapper.find('#default-receive-max-total-bytes').exists()).toBe(true)
    expect(input.exists()).toBe(true)
    expect(input.attributes('min')).toBe('0')
    expect(input.attributes('max')).toBeUndefined()
    expect(wrapper.text()).toContain('Use 0 for no limit.')
    expect(wrapper.find('#receive-notifications-enabled').exists()).toBe(true)
    expect(wrapper.find('#receive-upload-mode').exists()).toBe(false)
    expect(wrapper.find('#receive-upload-compression-enabled').exists()).toBe(false)
  })

  it('groups browser-managed download and upload engine settings under Transfer Engine', async () => {
    const wrapper = mountSettingsView()

    await openSettingsSection(wrapper, 'Transfer Engine')

    expect(activePanelTitle(wrapper)).toBe('Transfer Engine')
    expect(wrapper.find('#browser-managed-downloads-enabled').exists()).toBe(true)
    expect(wrapper.find('#browser-managed-download-memory').exists()).toBe(true)
    expect(wrapper.text()).toContain('Default: 64 MB.')
    expect(wrapper.find('#browser-managed-download-parallel-chunks').exists()).toBe(true)
    expect(wrapper.find('#browser-managed-compression-mode').exists()).toBe(true)
    expect(wrapper.find('#browser-download-encryption-policy').exists()).toBe(true)
    expect(wrapper.find('#receive-upload-mode').exists()).toBe(true)
    expect(wrapper.find('#receive-upload-compression-enabled').exists()).toBe(true)
    expect(wrapper.find('#receive-upload-encryption-policy').exists()).toBe(true)
    expect(wrapper.find('#receive-upload-chunk-sizing-mode').exists()).toBe(true)
    expect(wrapper.find('#receive-upload-max-body-size').exists()).toBe(true)
    expect(wrapper.find('#receive-page-title').exists()).toBe(false)
  })

  it('shows the auto probing threshold only for auto receive uploads', async () => {
    const settingsDraft = createSettingsDraft()
    const wrapper = mountSettingsView(settingsDraft)

    await openSettingsSection(wrapper, 'Transfer Engine')

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
    const wrapper = mountSettingsView(settingsDraft)

    await openSettingsSection(wrapper, 'Transfer Engine')

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

  it('warns when Cloudflare auto target request time is above 120 seconds', async () => {
    const settingsDraft = createSettingsDraft()
    settingsDraft.defaultPublishMode = 'QuickTunnel'
    settingsDraft.receiveUploadChunkSizingMode = 'Auto'
    settingsDraft.receiveUploadChunkTargetSeconds = 180
    const wrapper = mountSettingsView(settingsDraft)

    await openSettingsSection(wrapper, 'Transfer Engine')

    expect(wrapper.text()).toContain('Cloudflare can time out proxied requests after 120 seconds')
  })

  it('shows the receive upload chunk size setting in megabytes', async () => {
    const settingsDraft = createSettingsDraft()
    settingsDraft.receiveUploadChunkSizeBytes = 8 * 1024 * 1024
    const wrapper = mountSettingsView(settingsDraft)

    await openSettingsSection(wrapper, 'Transfer Engine')

    const input = wrapper.find('#receive-upload-chunk-size')

    expect(wrapper.text()).toContain('Packet size')
    expect(input.exists()).toBe(true)
    expect(input.element).toHaveProperty('value', '8')
    expect(input.attributes('min')).toBe('1')
    expect(wrapper.text()).toContain('Default: 16 MB')
  })

  it('shows the receive upload max body size setting in megabytes', async () => {
    const settingsDraft = createSettingsDraft()
    settingsDraft.receiveUploadMaxBodySizeBytes = 90 * 1024 * 1024
    const wrapper = mountSettingsView(settingsDraft)

    await openSettingsSection(wrapper, 'Transfer Engine')

    const input = wrapper.find('#receive-upload-max-body-size')

    expect(wrapper.text()).toContain('Max request body size')
    expect(input.exists()).toBe(true)
    expect(input.element).toHaveProperty('value', '90')
    expect(input.attributes('min')).toBe('1')
    expect(wrapper.text()).toContain('Cloudflare Free/Pro limit: 100 MB')
  })

  it('warns when Cloudflare Free plan max body size is above the plan limit', async () => {
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

    await openSettingsSection(wrapper, 'Transfer Engine')

    expect(wrapper.text()).toContain('Requests over 100 MB can fail with 413')
  })

  it('groups Explorer integration controls under Windows Integration', async () => {
    const wrapper = mountSettingsView()

    await openSettingsSection(wrapper, 'Windows Integration')

    expect(activePanelTitle(wrapper)).toBe('Windows Integration')
    expect(wrapper.find('#file-context-button').exists()).toBe(true)
    expect(wrapper.find('#folder-zip-context-button').exists()).toBe(true)
    expect(wrapper.find('#folder-browse-context-button').exists()).toBe(true)
    expect(wrapper.find('#folder-receive-context-button').exists()).toBe(true)
    expect(wrapper.find('#friendly-urls').exists()).toBe(false)
  })

  it('groups history retention, list pagination, and clear history action under History', async () => {
    const wrapper = mountSettingsView()

    await openSettingsSection(wrapper, 'History')

    expect(activePanelTitle(wrapper)).toBe('History')
    expect(wrapper.find('#history-retention-value').exists()).toBe(true)
    expect(wrapper.find('#history-items-per-page').exists()).toBe(true)
    expect(wrapper.find('#shares-items-per-page').exists()).toBe(true)
    expect(wrapper.find('button.danger').text()).toBe('Clear history')
  })

  it('keeps public transfer diagnostics toggles under Diagnostics', async () => {
    const wrapper = mountSettingsView()

    await openSettingsSection(wrapper, 'Diagnostics')

    expect(activePanelTitle(wrapper)).toBe('Diagnostics')
    expect(wrapper.find('#browser-transfer-diagnostics-enabled').exists()).toBe(true)
    expect(wrapper.find('#receive-transfer-diagnostics-enabled').exists()).toBe(true)
    expect(wrapper.find('#show-logs').exists()).toBe(true)
    expect(wrapper.find('#receive-upload-mode').exists()).toBe(false)
  })
})

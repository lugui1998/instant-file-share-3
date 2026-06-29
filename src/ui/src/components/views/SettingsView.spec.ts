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
    manualBindAddress: '127.0.0.1',
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

    expect(cardEyebrows.slice(-3)).toEqual(['Receiving', 'Cloudflare', 'Debug'])
  })
})

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { AgentShareRecord } from '../../agentBridge'
import SharesView from './SharesView.vue'

const activeShare = {
  id: 'share-1',
  token: 'token-1',
  fileName: 'guide.txt',
  filePath: 'C:\\files\\guide.txt',
  publicBaseUrl: 'http://127.0.0.1:46431',
  url: 'http://127.0.0.1:46431/s/token-1',
  fileSize: 5,
  itemKind: 'File',
  shareKind: 'File',
  canBrowseFolderContents: false,
  canDownloadFolderAsZip: false,
  primaryFolderEntryPoint: null,
  createdAtUtc: '2026-04-06T12:00:00Z',
  useCount: 0,
  state: 'Active',
  publishMode: 'Manual',
} satisfies AgentShareRecord

const receiveShare = {
  id: 'receive-1',
  token: 'receive-token',
  fileName: 'drop',
  filePath: 'C:\\files\\drop',
  publicBaseUrl: 'http://127.0.0.1:46431',
  url: 'http://127.0.0.1:46431/r/receive-token',
  fileSize: 0,
  itemKind: 'Receive',
  shareKind: null,
  canBrowseFolderContents: false,
  canDownloadFolderAsZip: false,
  primaryFolderEntryPoint: null,
  createdAtUtc: '2026-04-06T12:05:00Z',
  useCount: 0,
  maxTotalBytes: 1024,
  bytesReceived: 0,
  state: 'Active',
  publishMode: 'Manual',
} satisfies AgentShareRecord

function mountSharesView(shares: AgentShareRecord[] = [activeShare]) {
  return mount(SharesView, {
    props: {
      itemsPerPage: 25,
      shares,
    },
    global: {
      stubs: {
        PanelHeader: {
          template: '<div><slot /></div>',
        },
        TablePagination: true,
      },
    },
  })
}

describe('SharesView', () => {
  it('places copy link before show in explorer in the share actions', () => {
    const wrapper = mountSharesView()

    const labels = wrapper.findAll('.actions-cell button').map((button) => button.attributes('aria-label') ?? button.text())

    expect(labels).toEqual(['Copy link', 'Show in Explorer', 'Revoke'])
    expect(wrapper.get('button.danger').find('.trash-icon').exists()).toBe(true)
  })

  it('emits the selected share when copy link is clicked', async () => {
    const wrapper = mountSharesView()

    await wrapper.get('button[aria-label="Copy link"]').trigger('click')

    expect(wrapper.emitted('copyShare')).toEqual([[activeShare]])
  })

  it('emits the selected share id when show in explorer is clicked', async () => {
    const wrapper = mountSharesView()

    const button = wrapper.get('button[aria-label="Show in Explorer"]')
    await button.trigger('click')

    expect(button.text()).toBe('')
    expect(wrapper.emitted('showInExplorer')).toEqual([['share-1']])
  })

  it('shows receive links in the shares list with the same actions', async () => {
    const wrapper = mountSharesView([receiveShare])

    expect(wrapper.text()).toContain('Receive link')
    expect(wrapper.findAll('tbody tr:first-child td')[1].text()).toBe('--')

    await wrapper.get('button[aria-label="Copy link"]').trigger('click')
    await wrapper.get('button[aria-label="Show in Explorer"]').trigger('click')
    await wrapper.get('button.danger').trigger('click')

    expect(wrapper.emitted('copyShare')).toEqual([[receiveShare]])
    expect(wrapper.emitted('showInExplorer')).toEqual([['receive-1']])
    expect(wrapper.emitted('revokeShare')).toEqual([['receive-1']])
  })
})

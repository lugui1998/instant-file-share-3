import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import TransfersView from './TransfersView.vue'

function mountTransfersView(transfers: unknown[]) {
  return mount(TransfersView, {
    props: {
      transfers,
      itemsPerPage: 25,
      removingTransferId: null,
      clearingHistory: false,
    },
    global: {
      stubs: {
        HelpTooltip: {
          props: ['text'],
          template: '<span class="help-tooltip">{{ text }}</span>',
        },
        PanelHeader: {
          template: '<div><slot /></div>',
        },
        TablePagination: true,
      },
    },
  })
}

const baseTransfer = {
  id: 'transfer-1',
  shareId: 'share-1',
  token: 'token-1',
  fileName: 'team-files.zip',
  transferKind: 'FolderZipDownload',
  requesterName: null,
  remoteAddress: '203.0.113.10',
  startedAtUtc: '2026-04-06T12:00:00Z',
  lastUpdatedAtUtc: '2026-04-06T12:00:01Z',
  completedAtUtc: null,
  state: 'InProgress',
  isActive: true,
  succeeded: false,
  error: null,
} as const

describe('TransfersView', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('shows estimated progress for streamed ZIP downloads when source progress is available', () => {
    const wrapper = mountTransfersView([
      {
        ...baseTransfer,
        bytesSent: 128,
        totalBytes: 0,
        progressBytes: 512,
        progressTotalBytes: 1024,
      },
    ])

    expect(wrapper.text()).toContain('50% estimated')
    expect(wrapper.text()).toContain('512 B / 1 KB source processed')
    expect(wrapper.get('[role="progressbar"]').attributes('aria-valuenow')).toBe('50')
    expect(wrapper.get('.progress-fill').attributes('style')).toContain('width: 50%')
  })

  it('keeps the streaming fallback for older ZIP history without estimated progress', () => {
    const wrapper = mountTransfersView([
      {
        ...baseTransfer,
        bytesSent: 128,
        totalBytes: 0,
        progressBytes: 0,
        progressTotalBytes: 0,
      },
    ])

    expect(wrapper.text()).toContain('Streaming ZIP')
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)
  })

  it('does not show a progress bar for completed file downloads', () => {
    const wrapper = mountTransfersView([
      {
        ...baseTransfer,
        transferKind: 'FileDownload',
        bytesSent: 1024,
        totalBytes: 1024,
        progressBytes: null,
        progressTotalBytes: null,
        completedAtUtc: '2026-04-06T12:00:02Z',
        state: 'Completed',
        isActive: false,
        succeeded: true,
      },
    ])

    expect(wrapper.text()).toContain('100%')
    expect(wrapper.text()).toContain('Completed')
    expect(wrapper.text()).not.toContain('Stopped')
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)
  })

  it('does not show an empty progress bar for normal transfers with an unknown total', () => {
    const wrapper = mountTransfersView([
      {
        ...baseTransfer,
        transferKind: 'FileDownload',
        fileName: 'report.pdf',
        bytesSent: 2048,
        totalBytes: 0,
        progressBytes: null,
        progressTotalBytes: null,
        lastUpdatedAtUtc: '2026-04-06T12:00:02Z',
        state: 'Paused',
        isActive: false,
      },
    ])

    expect(wrapper.text()).toContain('2 KB / ?')
    expect(wrapper.text()).toContain('8 Kb/s')
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)
  })

  it('does not show an estimated progress bar for completed streamed ZIP downloads', () => {
    const wrapper = mountTransfersView([
      {
        ...baseTransfer,
        bytesSent: 2048,
        totalBytes: 0,
        progressBytes: 4096,
        progressTotalBytes: 4096,
        completedAtUtc: '2026-04-06T12:00:02Z',
        state: 'Completed',
        isActive: false,
        succeeded: true,
      },
    ])

    expect(wrapper.text()).toContain('100% estimated')
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false)
  })

  it('marks likely gateway addresses as unresolved router or proxy peers', () => {
    const wrapper = mountTransfersView([
      {
        ...baseTransfer,
        transferKind: 'FileUpload',
        remoteAddress: '192.168.1.1',
      },
    ])

    expect(wrapper.text()).toContain('Router/proxy')
    expect(wrapper.text()).toContain('192.168.1.1 reported by TCP peer')
    expect(wrapper.text()).toContain('without a forwarded client-IP header')
  })

  it('freezes the speed label when an in-progress transfer has stopped updating', async () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-04-06T12:00:04Z'))

    const wrapper = mountTransfersView([
      {
        ...baseTransfer,
        transferKind: 'FileDownload',
        fileName: 'report.pdf',
        bytesSent: 2048,
        totalBytes: 0,
        progressBytes: null,
        progressTotalBytes: null,
        lastUpdatedAtUtc: '2026-04-06T12:00:02Z',
        state: 'InProgress',
        isActive: true,
      },
    ])

    expect(wrapper.text()).toContain('Stopped')
    expect(wrapper.text()).toContain('2 KB / ?')
    expect(wrapper.text()).toContain('8 Kb/s')

    await vi.advanceTimersByTimeAsync(30_000)
    await nextTick()

    expect(wrapper.text()).toContain('2 KB / ?')
    expect(wrapper.text()).toContain('8 Kb/s')
  })
})

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import SharesView from './SharesView.vue'

describe('SharesView', () => {
  it('emits the selected share id when show in explorer is clicked', async () => {
    const wrapper = mount(SharesView, {
      props: {
        itemsPerPage: 25,
        shares: [
          {
            id: 'share-1',
            token: 'token-1',
            fileName: 'guide.txt',
            filePath: 'C:\\files\\guide.txt',
            publicBaseUrl: 'http://127.0.0.1:46431',
            fileSize: 5,
            shareKind: 'File',
            canBrowseFolderContents: false,
            canDownloadFolderAsZip: false,
            primaryFolderEntryPoint: null,
            createdAtUtc: '2026-04-06T12:00:00Z',
            useCount: 0,
            state: 'Active',
            publishMode: 'Manual',
          },
        ],
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

    const button = wrapper.get('button[aria-label=\"Show in Explorer\"]')
    await button.trigger('click')

    expect(button.text()).toBe('')
    expect(wrapper.emitted('showInExplorer')).toEqual([['share-1']])
  })
})

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import PublicShareShell from './PublicShareShell.vue'
import type { PublicSharePageModel } from '../types'

describe('PublicShareShell', () => {
  it('does not show the receive page description under the title', () => {
    const wrapper = mount(PublicShareShell, {
      props: {
        page: createPage('receive'),
      },
    })

    expect(wrapper.text()).toContain('Upload to lugui')
    expect(wrapper.text()).not.toContain('Upload files through Instant File Share.')
    expect(wrapper.find('.public-shell__brand-link').attributes('href')).toBe('https://github.com/lugui1998/instant-file-share-3')
  })

  it('keeps the description visible on file pages', () => {
    const wrapper = mount(PublicShareShell, {
      props: {
        page: createPage('file'),
      },
    })

    expect(wrapper.text()).toContain('Upload files through Instant File Share.')
    expect(wrapper.find('.public-shell__inline-link').attributes('href')).toBe('https://github.com/lugui1998/instant-file-share-3')
  })
})

function createPage(kind: PublicSharePageModel['kind']): PublicSharePageModel {
  return {
    kind,
    title: 'Upload to lugui',
    description: 'Upload files through Instant File Share.',
    canonicalUrl: 'https://example.test/r/token',
    siteName: 'Instant File Share',
    repositoryUrl: 'https://github.com/lugui1998/instant-file-share-3',
  }
}

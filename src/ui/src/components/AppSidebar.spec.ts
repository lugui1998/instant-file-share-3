import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import AppSidebar from './AppSidebar.vue'

vi.mock('../agentBridge', () => ({
  agentBridge: {
    getAppVersion: vi.fn().mockResolvedValue('1.3.0'),
  },
}))

describe('AppSidebar', () => {
  it('renders the title and app version without the legacy badge', async () => {
    const wrapper = mount(AppSidebar, {
      props: {
        activeView: 'shares',
        showLogs: false,
      },
    })

    await flushPromises()

    expect(wrapper.text()).toContain('Instant File Share')
    expect(wrapper.text()).toContain('v1.3.0')
    expect(wrapper.find('.brand-mark').exists()).toBe(false)
    expect(wrapper.find('.brand-link').attributes('href')).toBe('https://github.com/lugui1998/instant-file-share-3')
  })
})

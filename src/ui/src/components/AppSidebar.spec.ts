import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import AppSidebar from './AppSidebar.vue'

vi.mock('../agentBridge', () => ({
  agentBridge: {
    getAppVersion: vi.fn().mockResolvedValue('1.0.1'),
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
    expect(wrapper.text()).toContain('v1.0.1')
    expect(wrapper.find('.brand-mark').exists()).toBe(false)
  })
})

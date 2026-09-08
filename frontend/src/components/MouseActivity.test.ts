// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { expect, it } from 'vitest'
import MouseActivity from './MouseActivity.vue'

it('shows mouse counts without treating loading or failure as zero activity', async () => {
  const wrapper = mount(MouseActivity, { props: { counts: null, loading: true, failed: false } })
  expect(wrapper.text()).toContain('正在读取鼠标统计')
  expect(wrapper.find('dl').exists()).toBe(false)
  await wrapper.setProps({ loading: false, counts: { mouseLeft: 12, mouseRight: 3, mouseMiddle: 0, scrollUp: 4, scrollDown: 9 } })
  expect(wrapper.findAll('dd').map(n => n.text())).toEqual(['12次', '3次', '0次', '4次', '9次'])
  await wrapper.setProps({ counts: null, failed: true })
  expect(wrapper.find('dl').exists()).toBe(false)
  expect(wrapper.get('[role="alert"]').text()).toContain('读取失败')
  await wrapper.get('button').trigger('click')
  expect(wrapper.emitted('retry')).toHaveLength(1)
  wrapper.unmount()
})

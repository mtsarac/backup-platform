import { expect, it } from 'vitest'
import { parseListState } from './use-list-state'

const config = {
  search: true,
  filters: { status: ['ok', 'failed'], from: 'date', owner: 'text' },
  sort: { first: { name: 'asc', created: 'desc' }, default: { key: 'created', dir: 'desc' } },
} as const
const parse = (query: string, cfg: Parameters<typeof parseListState>[1] = config) => { const params = new URLSearchParams(query); return parseListState(name => params.get(name) ?? '', cfg) }

it('uses the default sort for an empty URL', () => {
  expect(parse('').sort).toEqual({ key: 'created', dir: 'desc' })
})

it('takes the first direction of a sort key given without one', () => {
  expect(parse('sort=name').sort).toEqual({ key: 'name', dir: 'asc' })
  expect(parse('sort=name&dir=desc').sort).toEqual({ key: 'name', dir: 'desc' })
  expect(parse('dir=asc').sort).toEqual({ key: 'created', dir: 'asc' })
})

it('ignores values the config does not allow', () => {
  const state = parse('sort=nope&dir=sideways&status=bad&from=2026-02-31&owner=x y')
  expect(state.sort).toEqual({ key: 'created', dir: 'desc' })
  expect(state.filters).toEqual({ status: '', from: '', owner: 'x y' })
  expect(parse('status=failed&from=2026-02-03').filters).toMatchObject({ status: 'failed', from: '2026-02-03' })
})

it('reads the search only when the list has one', () => {
  expect(parse('search=abc').search).toBe('abc')
  expect(parse('search=abc', { ...config, search: false }).search).toBe('')
})

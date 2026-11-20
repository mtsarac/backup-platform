import { expect, it } from 'vitest'
import { parseListState } from './use-list-state'
import { dayParam, isCompleteDay, pageParam, MAX_PAGE } from './url-params'

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

it('reads the search and sort from prefixed parameters', () => {
  const state = parse('attempts-search=abc&attempts-sort=name&search=zzz&sort=created', { ...config, paramPrefix: 'attempts-' })
  expect(state.search).toBe('abc')
  expect(state.sort).toEqual({ key: 'name', dir: 'asc' })
})

it('accepts only valid calendar dates and recognizes an incomplete typed year', () => {
  expect(dayParam('2024-02-29')).toBe('2024-02-29')
  expect(dayParam('2026-02-29')).toBe('')
  expect(dayParam('2026-13-01')).toBe('')
  expect(isCompleteDay('0002-11-19')).toBe(false)
  expect(isCompleteDay('2026-11-19')).toBe(true)
})

it('caps oversized pages and ignores invalid page numbers', () => {
  expect(pageParam('2147483648')).toBe(MAX_PAGE)
  expect(pageParam('2.5')).toBe(1)
  expect(pageParam('-1')).toBe(1)
})

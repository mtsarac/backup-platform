import { describe, expect, it } from 'vitest'
import { renderHook } from '@testing-library/react'
import { ApiError } from '@/lib/api/client'
import { isUncertainError, useIdempotencyKey } from './idempotency-key'

const rejected = new ApiError(409)
const unavailable = new ApiError(503)

describe('useIdempotencyKey', () => {
  it('gives the same body the same key and a different body a new one', () => {
    const { result } = renderHook(() => useIdempotencyKey())
    const first = result.current.keyFor({ a: 1 })
    expect(result.current.keyFor({ a: 1 })).toBe(first)
    expect(result.current.keyFor({ a: 2 })).not.toBe(first)
  })

  it('keeps the key for a retry after a 5xx or network failure', () => {
    const { result } = renderHook(() => useIdempotencyKey())
    const first = result.current.keyFor('body')
    result.current.settle(unavailable)
    expect(result.current.keyFor('body')).toBe(first)
    result.current.settle(new TypeError('network'))
    expect(result.current.keyFor('body')).toBe(first)
  })

  it('starts a new operation after success or a definite rejection', () => {
    const { result } = renderHook(() => useIdempotencyKey())
    const first = result.current.keyFor('body')
    result.current.settle()
    const second = result.current.keyFor('body')
    expect(second).not.toBe(first)
    result.current.settle(rejected)
    expect(result.current.keyFor('body')).not.toBe(second)
  })

  it('reset forgets the key even for the same body', () => {
    const { result } = renderHook(() => useIdempotencyKey())
    const first = result.current.keyFor('body')
    result.current.reset()
    expect(result.current.keyFor('body')).not.toBe(first)
  })
})

describe('isUncertainError', () => {
  it('is true only for a failure whose outcome the server may have recorded', () => {
    expect(isUncertainError(null)).toBe(false)
    expect(isUncertainError(rejected)).toBe(false)
    expect(isUncertainError(unavailable)).toBe(true)
    expect(isUncertainError(new Error('offline'))).toBe(true)
  })
})

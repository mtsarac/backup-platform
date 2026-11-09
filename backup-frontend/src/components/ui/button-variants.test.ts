import { describe, expect, it } from 'vitest'
import { buttonVariants } from './button-variants'

describe('buttonVariants', () => {
  it.each(['xs', 'sm', 'icon-xs', 'icon-sm'] as const)('keeps a %s button at 44 px on touch screens', (size) => {
    expect(buttonVariants({ size })).toMatch(/pointer-coarse:(min-h|size)-11/)
  })
})

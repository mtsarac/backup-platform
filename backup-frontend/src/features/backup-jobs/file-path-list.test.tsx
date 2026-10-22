import { describe, expect, it } from 'vitest'
import { stepOfField } from './job-wizard-model'
import { validFilePath, validFilePaths } from './file-path-list'

describe('confined file selections', () => {
  it.each(['../escape', '/absolute', 'C:/absolute', 'a\\b', 'a//b', 'a/./b', 'a/../b', 'a\u0000b', 'a\u0085b', ''])('rejects unsafe path %j', path => {
    expect(validFilePath(path)).toBe(false)
  })
  it('allows relative files, folders and root, but not root as a wildcard filter', () => {
    expect(validFilePaths(['.', 'reports/2026.txt'])).toBe(true)
    expect(validFilePaths(['**/*.txt', 'logs/?/data*'], false, false)).toBe(true)
    expect(validFilePaths(['.'], false, false)).toBe(false)
    expect(validFilePaths([])).toBe(false)
    expect(validFilePaths([], false, false)).toBe(true)
    expect(validFilePaths(Array.from({ length: 257 }, () => 'file'))).toBe(false)
    expect(validFilePath('a'.repeat(1025))).toBe(false)
  })
  it('routes backend file-policy errors to the source step', () => {
    expect(stepOfField('configuration.files')).toBe(1)
    expect(stepOfField('configuration.files.paths[0]')).toBe(1)
    expect(stepOfField('configuration.files.symlinkPolicy')).toBe(1)
  })
})

import { describe, expect, it } from 'vitest'
import { initialValues, switchEngine, type EngineMemory } from './job-form-values'
import { changedSections, jobSteps, stepOfField, validateJob, validateStep } from './job-wizard-model'

describe('job steps', () => {
  it('has five steps in order', () => {
    expect(jobSteps).toEqual(['source', 'storage', 'schedule', 'protection', 'review'])
  })
  it.each([
    ['name', 'source'], ['agentId', 'source'], ['configuration.host', 'source'], ['configuration.databaseLogin.password', 'source'], ['configuration.files.paths[0]', 'source'],
    ['configuration.destinations[0].storageConnectionId', 'storage'], ['configuration.requiredVerifiedReplicaCount', 'storage'],
    ['schedule.cronExpression', 'schedule'], ['retryPolicy.maxAttempts', 'schedule'], ['retention.keepForSeconds', 'schedule'], ['overlapPolicy', 'schedule'],
    ['configuration.encryptionPolicy', 'protection'], ['configuration.checksumPolicy', 'protection'], ['configuration.copyOnly', 'protection'],
  ] as const)('routes the server field %s to %s', (field, step) => {
    expect(stepOfField(field)).toBe(step)
  })
  it('does not claim a field it merely shares a prefix with, nor an unknown one', () => {
    expect(stepOfField('configuration.encrypt')).toBe('source')
    expect(stepOfField('configuration.encryptionPolicy')).toBe('protection')
    expect(stepOfField('configuration.unknown')).toBeUndefined()
  })
})

describe('step validation', () => {
  const values = initialValues()
  it('asks for name and computer on the first step only', () => {
    expect(Object.keys(validateStep('source', values))).toEqual(expect.arrayContaining(['name', 'agentId', 'configuration.host', 'configuration.database']))
    expect(validateStep('storage', values)).toEqual({})
    expect(validateStep('protection', values)).toEqual({})
  })
  it('reports the first invalid step of a whole job', () => {
    expect(validateJob(values).firstInvalid).toBe('source')
    const valid = { ...values, name: 'n', agentId: 'a', configuration: { ...values.configuration, host: 'h', database: 'd' } }
    expect(validateJob(valid)).toEqual({ errors: {}, firstInvalid: undefined })
    expect(validateJob({ ...valid, retry: { ...valid.retry, maxAttempts: 0 } }).firstInvalid).toBe('schedule')
  })
})

describe('engine switching', () => {
  it('gives back what was typed for an engine when returning to it', () => {
    const start = { ...initialValues().configuration, host: 'sql', database: 'ERP', port: 1450, integratedSecurity: false, copyOnly: false, checksumPolicy: 'Disabled' as const }
    const toPg = switchEngine(start, {} as EngineMemory, 'postgres')
    expect(toPg.configuration).toMatchObject({ engine: 'postgres', host: '', database: '', port: 5432, checksumPolicy: 'Preferred' })
    const back = switchEngine({ ...toPg.configuration, host: 'pg' }, toPg.memory, 'mssql')
    expect(back.configuration).toMatchObject({ engine: 'mssql', host: 'sql', database: 'ERP', port: 1450, integratedSecurity: false, copyOnly: false, checksumPolicy: 'Disabled' })
    expect(switchEngine(back.configuration, back.memory, 'postgres').configuration.host).toBe('pg')
  })
  it('leaves settings owned by other steps alone', () => {
    const start = { ...initialValues().configuration, compressionPolicy: 'Disabled' as const, encryptionPolicy: 'Required' as const, storagePrefix: 'x', encrypt: false }
    expect(switchEngine(start, {}, 'file-system').configuration).toMatchObject({ compressionPolicy: 'Disabled', encryptionPolicy: 'Required', storagePrefix: 'x', encrypt: false, host: 'localhost' })
  })
})

describe('changed sections', () => {
  it('names only the sections that differ from the saved values', () => {
    const saved = initialValues()
    expect(changedSections(saved, saved)).toEqual([])
    const edited = { ...saved, retention: { ...saved.retention, keepForSeconds: 1 }, configuration: { ...saved.configuration, encryptionPolicy: 'Required' as const, storagePrefix: 'p' } }
    expect(changedSections(saved, edited)).toEqual(['storage', 'schedule', 'protection'])
  })
})

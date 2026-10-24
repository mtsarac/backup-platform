import { describe, expect, it } from 'vitest'
import type { TransferScheduleWindowDto } from './types'
import { describeSchedule } from './schedule'

const MBIT_10 = 1_250_000
const window = (dayOfWeek: number, startTime = '09:00:00', endTime = '18:00:00', bytesPerSecond = MBIT_10): TransferScheduleWindowDto => ({ dayOfWeek, startTime, endTime, bytesPerSecond })
const days = (list: number[], ...rest: [string?, string?, number?]) => list.map(day => window(day, ...rest))

describe('bandwidth profile schedule text', () => {
  it('reads Monday to Friday as "Hafta içi" with the friendly rate and no raw bytes', () => {
    expect(describeSchedule(days([1, 2, 3, 4, 5]))).toEqual(['Hafta içi 09:00–18:00: 10 Mbit/sn'])
  })

  it('reads all seven days as every day and Saturday plus Sunday as the weekend', () => {
    expect(describeSchedule(days([0, 1, 2, 3, 4, 5, 6]))).toEqual(['Her gün 09:00–18:00: 10 Mbit/sn'])
    expect(describeSchedule(days([6, 0]))).toEqual(['Hafta sonu 09:00–18:00: 10 Mbit/sn'])
  })

  it('names the days of any other combination, Monday first and Sunday last', () => {
    expect(describeSchedule(days([0, 3, 1]))).toEqual(['Pazartesi, Çarşamba, Pazar 09:00–18:00: 10 Mbit/sn'])
    expect(describeSchedule(days([5]))).toEqual(['Cuma 09:00–18:00: 10 Mbit/sn'])
  })

  it('says a zero-rate window pauses the transfer instead of showing a rate', () => {
    expect(describeSchedule(days([1], '12:00:00', '13:00:00', 0))).toEqual(['Pazartesi 12:00–13:00: Aktarımı Duraklat'])
  })

  it('keeps windows with different hours or rates on separate lines in time order', () => {
    const lines = describeSchedule([
      ...days([1, 2, 3, 4, 5], '18:00:00', '23:00:00', 12_500_000),
      ...days([1, 2, 3, 4, 5]),
      ...days([6, 0], '00:00:00', '23:59:00', 125_000),
    ])
    expect(lines).toEqual([
      'Hafta sonu 00:00–23:59: 1 Mbit/sn',
      'Hafta içi 09:00–18:00: 10 Mbit/sn',
      'Hafta içi 18:00–23:00: 100 Mbit/sn',
    ])
  })

  it('has no lines for a profile without windows', () => {
    expect(describeSchedule([])).toEqual([])
  })
})

/** The download history's list state; the platform page also reads it to open the history while it is filtered. */
export const downloadAttemptsListConfig = {
  search: true,
  filters: { result: ['Completed', 'Failed', 'Cancelled', 'Refused'], shareId: 'text', artifactId: 'text', userId: 'text' },
  sort: { first: { tenant: 'asc', artifact: 'asc', user: 'asc', startedAt: 'desc', endedAt: 'desc', result: 'asc', bytes: 'desc' }, default: { key: 'startedAt', dir: 'desc' } },
  // The page also holds the shares list, so this list keeps its own search, sort and page parameters.
  pageKey: 'attempts-page',
  paramPrefix: 'attempts-',
} as const

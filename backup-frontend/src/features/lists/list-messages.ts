import { tx } from '@/lib/i18n'

/** For the `DataList` of a server-paged list that is only reached while `totalCount > 0`: a page with no rows then is a page past the last, which `ListPager` is already leaving. */
export const pastLastPageMessage = () => tx('Bu sayfada kayıt yok.')

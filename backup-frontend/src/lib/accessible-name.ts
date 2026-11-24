import { currentLocale, tx } from '@/lib/i18n'

/** WCAG 2.5.3 (label in name): the accessible name of an action button starts with its visible text, then names the subject ("Kaldır — Ayşe"). */
export function actionName(action: string, subject: string): string {
  return tx("{0} — {1}", { "0": action.charAt(0).toLocaleUpperCase(currentLocale()) + action.slice(1), "1": subject })
}

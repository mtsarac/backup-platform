import { fireEvent } from '@testing-library/react'

/** Types into a list's search box and leaves it: the box commits to the URL on blur, where typing alone waits for the debounce. */
export function searchFor(box: HTMLElement, value: string) {
  fireEvent.change(box, { target: { value } })
  fireEvent.blur(box)
}

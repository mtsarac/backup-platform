import { useLocation } from 'react-router-dom'

/** Test helper: exposes the router location so URL-backed list state can be asserted. */
export function LocationProbe() {
  const location = useLocation()
  return <div data-testid="location">{location.pathname}{location.search}</div>
}

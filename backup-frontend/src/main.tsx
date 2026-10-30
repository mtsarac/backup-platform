import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { applyA11yPrefs } from './lib/a11y-prefs'
import { Providers } from './lib/providers'
import { loadFeatureFlags } from './features/feature-flags/feature-flags'

applyA11yPrefs()

void loadFeatureFlags().then(() => createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Providers>
      <App />
    </Providers>
  </StrictMode>,
))

Backup Platform Web UI @VERSION@
================================

Static files of the React management UI (Vite production build).

Serve the dist/ folder from any static web server on the SAME ORIGIN as the backend and route /api to the backend:
the UI uses cookie authentication with an antiforgery header and calls the relative path /api. Single-page-app
routing needs every unknown path to fall back to dist/index.html.

Verify the download with the SHA256SUMS.txt file attached to the release.

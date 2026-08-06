# Frontend guidance

This subtree is the React 18/TypeScript UI. It uses Material UI, TanStack Query, React Router, date-fns, npm, and Vite. API access is centralized in `src/api/client.ts`, shared response/request shapes belong in `src/types/api.ts`, server state belongs in `src/hooks/`, and route-level composition belongs in `src/pages/`.

Run commands from `frontend/`:

```text
npm ci
npm run dev
npm run build
npm run preview
```

`npm run build` is the available type-check and production-build verification. No frontend test, lint, or formatting command is defined.

Follow the existing strict TypeScript and functional React style: two-space indentation, semicolons, single quotes, typed API results, TanStack Query hooks for server state, and Material UI components/theme tokens for presentation. Reuse the shared timezone/date-format helpers for displayed dates and times.

Do not edit `node_modules/`, `.vite/`, or root `wwwroot/`; they are generated. The Vite build empties and recreates `../wwwroot`. Preserve backend API contracts and update `src/types/api.ts` with any intentional contract change.

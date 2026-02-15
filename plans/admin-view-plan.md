## Plan: Admin User Overview

En adminvy med lösenordsbaserad bootstrap och persisterande admin-rättigheter per användare. Lösenordet sätts via `PRISSTYRNING_Admin__Password`. Väl inne kan man ge sin användare permanent admin-access – sedan behövs lösenordet inte längre.

**Autentiseringsflöde:**
1. Användaren går till `/admin` → kontrollerar om dess userId har admin-flagga → om ja, direkt åtkomst
2. Om nej → visa lösenordsfält → valideras mot `PRISSTYRNING_Admin__Password`
3. Vid korrekt lösenord → användarens userId markeras som admin (sparas i `data/admin.json`)
4. Framtida besök: admin-flaggan erkänns utan lösenord
5. Från adminvyn kan man ge/ta bort admin-rättigheter för vilken användare som helst

**Phases (3)**

1. **Phase 1: Backend Admin API med auth**
    - **Objective:** Skapa admin-API med dubbelt auth-stöd: antingen `X-Admin-Password` header ELLER att användarens userId finns i `data/admin.json`. Endpoints:
      - `GET /api/admin/users` – lista alla användare med settings, zon, Daikin-status, schemahistorik
      - `POST /api/admin/login` – verifiera lösenord, markera aktuell användare som admin
      - `GET /api/admin/status` – returnerar om aktuell användare har admin-åtkomst
      - `POST /api/admin/users/{userId}/grant` – ge en användare admin-access
      - `DELETE /api/admin/users/{userId}/grant` – ta bort admin-access
    - **Files/Functions to Modify/Create:**
      - `AdminService.cs` (ny) – admin-persistens (läs/skriv `data/admin.json`)
      - `Program.cs` – admin endpoint-grupp med auth-middleware
      - `StoragePaths.cs` – ny metod `GetAdminJsonPath`
    - **Tests to Write:**
      - `Admin_NoAuth_Returns401`
      - `Admin_PasswordLogin_GrantsAccess`
      - `Admin_PersistedAdmin_HasAccess`
      - `Admin_ListUsers_ReturnsAllUsers`
      - `Admin_GrantRevoke_Works`
      - `Admin_NoPasswordConfigured_Returns403`
    - **Steps:**
      1. Skriv tester i `Prisstyrning.Tests/Api/AdminEndpointTests.cs`
      2. Kör testerna (röda)
      3. Implementera `AdminService.cs` med `IsAdmin`, `GrantAdmin`, `RevokeAdmin`, `GetAdminUserIds`
      4. Lägg till `GetAdminJsonPath` i `StoragePaths.cs`
      5. Implementera admin-endpoints i `Program.cs`
      6. Kör testerna (gröna)

2. **Phase 2: Frontend Admin Page**
    - **Objective:** Skapa `/admin` med: först check via `GET /api/admin/status`, om admin → visa direkt, annars → lösenordsfält. Tabell med alla användare, markera aktuell användare, admin toggle per användare.
    - **Files/Functions to Modify/Create:**
      - `frontend/src/pages/AdminPage.tsx` (ny)
      - `frontend/src/App.tsx` – ny route
      - `frontend/src/components/Layout.tsx` – navigationsknapp
      - `frontend/src/api/client.ts` – admin API-metoder
      - `frontend/src/types/api.ts` – admin-typer
    - **Tests to Write:** Inga (frontend-tester ej uppsatta)
    - **Steps:**
      1. Lägg till TypeScript-typer för admin-API-svar i `api.ts`
      2. Lägg till API-metoder i `client.ts` med `X-Admin-Password` header-stöd
      3. Skapa `AdminPage.tsx` med login-flöde och MUI Table
      4. Lägg till route i `App.tsx` och nav-knapp i `Layout.tsx`

3. **Phase 3: Delete User Action**
    - **Objective:** `DELETE /api/admin/users/{userId}` (admin-skyddat) + delete-knapp i tabellen med bekräftelsedialog. Kan inte ta bort sig själv.
    - **Files/Functions to Modify/Create:**
      - `Program.cs` – nytt DELETE endpoint
      - `frontend/src/pages/AdminPage.tsx` – delete-knapp med dialog
      - `frontend/src/api/client.ts` – delete-metod
    - **Tests to Write:**
      - `AdminDeleteUser_RemovesData`
      - `AdminDeleteUser_CannotDeleteSelf`
      - `AdminDeleteUser_NonExistent_Returns404`
    - **Steps:**
      1. Skriv tester för delete-endpoint
      2. Implementera `DELETE /api/admin/users/{userId}` som raderar `data/tokens/{userId}/` och `data/schedule_history/{userId}/`
      3. Lägg till delete-knapp med MUI confirm-dialog i frontend

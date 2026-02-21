## Phase 2 Complete: Improved issuer validation and subject extraction

Relaxed the strict `daikineurope.com` issuer check to accept tokens where either the issuer contains "daikin" OR the audience matches the expected clientId. This prevents silently skipping OAuth deduplication when the IDP uses a non-standard issuer URL.

**Files created/changed:**
- DaikinOAuthService.cs
- Prisstyrning.Tests/Integration/DaikinOAuthServiceIntegrationTests.cs

**Functions created/changed:**
- `ExtractSubjectFromIdToken` — new trust-or-audience validation logic
- `RedactIssuerHost` — new helper for safe logging of issuer

**Tests created/changed:**
- ExtractSubjectFromIdToken_WithDaikinIssuer_ReturnsSubject (renamed)
- ExtractSubjectFromIdToken_WithAlternateIssuerButMatchingAud_ReturnsSubject (new)
- ExtractSubjectFromIdToken_WithAlternateIssuerAndNoAud_ReturnsNull (new)
- ExtractSubjectFromIdToken_WithMissingIssuerButMatchingAud_ReturnsSubject (new)
- ExtractSubjectFromIdToken_WithWrongIssuer_ReturnsNull (updated semantics)
- ExtractSubjectFromIdToken_WithMissingIssuer_ReturnsNull (updated semantics)

**Review Status:** APPROVED

**Git Commit Message:**
fix: relax id_token issuer validation for OAuth dedup

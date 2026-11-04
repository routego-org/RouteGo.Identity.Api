# CAP-PRF-001 — GetMyProfile

- Capability: CAP-PRF-001
- Canonical spec: `capabilities/profile/features/my-profile.md` (spec 0.1.0)
- Area: Profile
- Slice: `Features/Profile/GetMyProfile/`

## Contract

Implemented verbatim from `contracts/profile/profile.yaml`.

- Operation: `getMyProfile`
- Route / verb: `GET /me` — bearer access token, any role
- Request: none; the account is the access token's `sub`
- Response: `200` `Profile` (`userId`, `email`, `fullName`, `phone`, `role`)
- Problem codes: `UNAUTHORIZED` (401)

## Acceptance criteria

Each criterion maps to tests in `GetMyProfile.Tests.cs`.

- [x] AC-1: view my profile — `GetMyProfile_SignedInCustomer_ReturnsTheirOwnProfile`
- [x] AC-2: must be signed in — `GetMyProfile_WithoutAnAccessToken_IsUnauthorized`, `GetMyProfile_WithAnExpiredAccessToken_IsUnauthorized`

## Invariants touched

- An account sees only its own profile: the account id comes from the token, never from the request.

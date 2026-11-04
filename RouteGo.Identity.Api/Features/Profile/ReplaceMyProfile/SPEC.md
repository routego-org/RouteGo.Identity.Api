# CAP-PRF-002 — ReplaceMyProfile

- Capability: CAP-PRF-002
- Canonical spec: `capabilities/profile/features/my-profile.md` (spec 0.1.0)
- Area: Profile
- Slice: `Features/Profile/ReplaceMyProfile/`

## Contract

Implemented verbatim from `contracts/profile/profile.yaml`.

- Operation: `replaceMyProfile`
- Route / verb: `PUT /me` — bearer access token, any role
- Request: `ProfileReplacement` (`fullName`, `phone` — both required, `phone` may be null)
- Response: `200` `Profile`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401)

## Acceptance criteria

Each criterion maps to tests in `ReplaceMyProfile.Tests.cs`.

- [x] AC-3: replace name and phone — `ReplaceMyProfile_NewNameAndPhone_StoresAndReturnsTheUpdatedProfile`
- [x] AC-4: clearing the phone — `ReplaceMyProfile_PhoneNull_ClearsThePhone`
- [x] AC-5: phone normalised — `ReplaceMyProfile_PhoneWithoutPrefix_IsKeptAndReturnedWithPlus91`
- [x] AC-6: validation (phone omitted, name "A", phone "12345") — `ReplaceMyProfile_InvalidReplacement_IsRefusedNamingTheFieldAndChangesNothing`
- [x] AC-7: email, role and status are ignored — `ReplaceMyProfile_WithEmailAndRole_IgnoresThem`
- [x] AC-8: must be signed in — `ReplaceMyProfile_WithoutAnAccessToken_IsUnauthorized`

## Invariants touched

- Full name and phone rules: `Domain/AccountRules.cs` (shared with registration and admin creation).
- The UPDATE names only `full_name` and `phone`, so nothing else can change whatever the body carries.

## Notes

- PUT is a full replacement: a missing `phone` is refused, an explicit `null` clears it. The request
  records whether the field was present, because the two are indistinguishable once deserialized.
- The signed-in account is the audit actor (the scoped writer connection).

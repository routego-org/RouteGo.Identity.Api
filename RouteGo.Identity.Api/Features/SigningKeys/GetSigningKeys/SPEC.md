# CAP-KEY-001 — Get signing keys

- Capability: CAP-KEY-001
- Canonical spec: `capabilities/signing-keys/features/public-signing-keys.md` (spec 0.1.0)
- Area: SigningKeys
- Slice: `Features/SigningKeys/GetSigningKeys/`

## Contract

Implemented verbatim from `contracts/signing-keys/signing-key.yaml`.

- Operation: `getSigningKeys`
- Route / verb: `GET /.well-known/jwks.json` — the one route outside the plural-noun convention (ADR 0002)
- Request: none; public (`security: []`)
- Response: `200` `PublicKeySet` — `keys[]` of `PublicKey` (`kty`, `use`, `alg`, `kid`, `n`, `e`)
- Problem codes: none (`emits: []`)

## Acceptance criteria

Each criterion maps to one test in `GetSigningKeys.Tests.cs`.

- [x] AC-1: the key set lists every published key as RSA / sig / RS256 — `GetSigningKeys_ActiveAndIntroducedKeys_ListsBothAsRs256SigningKeys`
- [x] AC-2: a previous key stays until retired — `GetSigningKeys_PreviousKey_StaysPublishedUntilRetired`
- [x] AC-3: retired keys are gone — `GetSigningKeys_RetiredKey_IsNotPublished`
- [x] AC-4: no secret material — `GetSigningKeys_AnyKey_CarriesNoPrivateComponent`
- [x] AC-5: public — `GetSigningKeys_WithoutAnAccessToken_IsReturned`
- [x] AC-6: a token validates with the key set alone, a tampered copy fails — `GetSigningKeys_TokenFromSignIn_ValidatesWithTheKeySetAloneAndATamperedCopyFails`

## Invariants touched

- Which states are published: `Domain/SigningKeyStates.Published` (Introduced, Active, Previous).

## Drift (to raise upstream)

- The contract's descriptions say the set holds "introduced or active" keys; the feature spec
  (business rule 1, AC-2) and the data model include Previous keys. The feature spec is implemented.
- The contract sets `minItems: 1`; before the first key is introduced the set is empty. Identity is
  not ready (`/health/ready` 503) in that state, so no consumer should be routed to it.

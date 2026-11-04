# CAP-KEY-002 — Rotate signing key

- Capability: CAP-KEY-002
- Canonical spec: `capabilities/signing-keys/features/signing-key-rotation.md` (spec 0.1.0)
- Area: SigningKeys
- Slice: `Features/SigningKeys/RotateSigningKey/`

## Contract

`kind: worker`, trigger `manual: signing-key rotation stage` — no HTTP operation. The platform
engineer runs one stage as a one-off container of the Identity image:

```
rotate-key --stage introduce|activate|retire --key-id <key ID> --by <name>
```

- Exit 0: the stage completed, or had already completed (nothing changed, no audit record).
- Exit 2: refused, nothing changed. Prints the cataloged worker code:
  `SIGNING_KEY_NOT_YET_PROPAGATED` (with `activationAllowedAt`), `SIGNING_KEY_RETIREMENT_TOO_EARLY`
  (with `retirementAllowedAt`), `SIGNING_KEY_STAGE_NOT_ALLOWED` (with `currentState`), or that the
  private half in the secret store is missing or unusable (introduce only).
- Exit 64: the command line was not understood.
- Private halves are read from `routego/identity/jwt-signing-key/<keyId>` (plain PEM); the public
  half is derived from it and stored. No private material is ever received through a request.
- Settings: `SigningKeyActivationWaitMinutes` (SSM `signing-key-activation-wait-minutes`, default 10)
  and `AccessTokenMinutes` (SSM `access-token-minutes`, default 15).

## Acceptance criteria

Each criterion maps to tests in `RotateSigningKey.Tests.cs`.

- [x] AC-1: introduce a new key — `HandleAsync_IntroduceNewKey_PublishesItWithoutSigningAndRecordsTheAudit`
- [x] AC-2: activate after the wait — `HandleAsync_ActivateAfterTheWait_SwitchesSigningAndKeepsTheOldKeyPublished`
- [x] AC-3: activate too early — `HandleAsync_ActivateTooEarly_IsRefusedWithTheTimeItBecomesAllowed`
- [x] AC-4: retire after one access-token lifetime — `HandleAsync_RetireAfterOneTokenLifetime_RemovesTheKeyFromPublication`
- [x] AC-5: retire too early — `HandleAsync_RetireTooEarly_IsRefusedWithTheTimeItBecomesAllowed`
- [x] AC-6: a stage that does not fit the key's state — `HandleAsync_RetireTheActiveKey_IsNotAllowedAndCarriesItsState`, `HandleAsync_ActivateAKeyNeverIntroduced_IsNotAllowed`, `HandleAsync_IntroduceWhileAnotherKeyIsIntroduced_IsNotAllowed`
- [x] AC-7: repeat safety — `HandleAsync_ActivateRunTwice_ChangesNothingTheSecondTime`
- [x] AC-8: no downtime across a full rotation — `FullRotation_SignedInBeforeActivate_TokensKeepValidatingAndTheSessionKeepsRefreshing`
  (`RotateSigningKey.NoDowntime.Tests.cs`)
- Business rule "if the private half cannot be read, introduce is refused" —
  `HandleAsync_IntroduceWithNoPrivateKeyInTheStore_IsRefusedAndPublishesNothing`, `HandleAsync_IntroduceWithAMalformedSecret_IsRefused`,
  `HandleAsync_IntroduceWithAPemWhoseBodyIsCorrupt_IsRefused`, `HandleAsync_IntroduceWithOnlyAPublicKeyStored_IsRefusedAndPublishesNothing`,
  `HandleAsync_IntroduceWithA4096BitKey_IsRefused` (the key rule itself is `Domain/SigningKeyMaterial.cs`, shared with readiness)

## Invariants touched

- Key states and publication: `Domain/SigningKeyStates.cs`.
- At most one Active and at most one Introduced key: partial unique indexes on `signing_keys.signing_keys`.
- A stage and its AuditRecorded event commit in one transaction (`IdentityEventRecorder`).

## Drift (to raise upstream)

- **First key.** When no key is Active (a fresh deployment), activate does not wait the minimum
  time: no token exists, so no consumer cache can be stale, and waiting would leave sign-in down.
  The spec does not state this case. Test: `HandleAsync_FirstKeyWithNoActiveKey_ActivatesWithoutTheWait`.
- **Contract wording.** `contracts/signing-keys/signing-key.yaml` describes the key set as "introduced
  or active"; the feature spec and data model include Previous keys too (AC-2 of CAP-KEY-001). The
  feature spec is followed.
- Introducing a key ID that already exists in a later state is refused as `SIGNING_KEY_STAGE_NOT_ALLOWED`
  (key IDs are never reused); the spec names only the "another key is already introduced" case.

## Notes

- Database writes are attributed to the system actor with action `job:RotateSigningKey`; the
  AuditRecorded event names the engineer (`actorId` = `--by`, `actorRole` = `PlatformEngineer`).
- Stages serialize on a transaction-scoped advisory lock.

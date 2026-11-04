# CAP-AUTH-007 — SearchUsers

- Capability: CAP-AUTH-007
- Canonical spec: `capabilities/authentication/features/admin-user-search.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/SearchUsers/`

## Contract

Implemented verbatim from `contracts/authentication/user.yaml`.

- Operation: `searchUsers`
- Route / verb: `GET /auth/admin/users` — bearer access token, role Admin
- Request: query `email`, `role`, `status`, `page`, `pageSize`, `sort`, `desc`
- Response: `200` `UserListResponse` (`items` of `UserSummary`, `totalCount`, `page`, `pageSize`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403)

## Acceptance criteria

Each criterion maps to tests in `SearchUsers.Tests.cs`.

- [x] AC-1: search by partial email, any case — `SearchUsers_PartialEmailInAnyCase_ReturnsTheContainingAccounts`
- [x] AC-2: filter by role and status — `SearchUsers_RoleAndStatusFilters_ReturnOnlyAccountsMatchingBoth`
- [x] AC-3: default order and paging — `SearchUsers_SecondPageOfTwenty_ReturnsAccounts21To40NewestFirst`
- [x] AC-4: no match is an empty page — `SearchUsers_NoMatch_ReturnsAnEmptyPage`
- [x] AC-5: only admins — `SearchUsers_SignedInCustomer_IsForbidden`
- [x] AC-6: must be signed in — `SearchUsers_WithoutAnAccessToken_IsUnauthorized`
- [x] AC-7: invalid filter — `SearchUsers_InvalidFilter_IsRefusedNamingIt` (status "deleted", page size 500, unknown sort field)

## Invariants touched

- Role and status filters accept exactly the contract's camelCase spellings (`WireJson.TryReadEnum`).
- Sort fields map to columns through a fixed list; the caller's text never reaches the SQL.

## Notes

- With no `sort`, the order is newest account first; with a `sort`, `desc` picks the direction.
  The account id breaks ties so pages are stable.
- The email filter is `strpos(lower(email), lower(@Email))`, so `%` and `_` in the input are literal.

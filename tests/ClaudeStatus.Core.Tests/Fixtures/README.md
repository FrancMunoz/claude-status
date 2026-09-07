# Fixtures — `GET /api/oauth/usage`

Recorded responses used to test `ClaudeSubscriptionUsageProvider` without any
network access.

| File | What it covers |
|---|---|
| `usage-normal.json` | **Real capture**, 2026-09-04, Max plan. Pretty-printed, otherwise byte-faithful. The canonical shape. |
| `usage-no-scoped-window.json` | Plan with no `weekly_scoped` entry — `WeekFable` must come out null, not throw. |
| `usage-legacy-flat-only.json` | No `limits` array at all — parser must fall back to the flat `five_hour` / `seven_day` keys. |
| `usage-unknown-shapes.json` | Unknown `kind`, unknown scoped model, unknown top-level keys, `null` `resets_at`, float percents. Nothing here may break parsing. |
| `error-rate-limited.json` | The 429 body. Note it is also what an **unauthenticated** request returns. |

## Rules

- **Never** record a fixture without checking it for credential material first.
  The response body carries no token, but the *response headers* do carry
  `anthropic-organization-id` and `anthropic-workspace-id` — never record headers
  verbatim, and never log them.
- Timestamps in `usage-normal.json` are the real ones from the capture and are
  therefore in the past. Tests must inject a clock rather than using
  `DateTimeOffset.UtcNow`, otherwise "resets in Xh Ym" assertions rot.
- The derived fixtures use obviously-synthetic values (year 2030) so they never
  read as real account data.

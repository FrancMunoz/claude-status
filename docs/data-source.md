# Data source — subscription usage endpoint (Phase 1 spike findings)

Status: **undocumented / unofficial**. Reverse-engineered by the community from
Claude Code's bundled `cli.js`; used by ccusage, Claude-Code-Usage-Monitor,
claude-usage-panel, claude-usage-widget, claude-codex-battery and Claude Code's
own `/usage` and `/status` commands. May change or break without notice.
Last verified: **2026-09-04 by a live call from this repo** (200 OK, Max plan,
`req_011CeiYQXCFiHHLt96fGrNDU`). Sources listed at the end.

## Endpoint

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken>          # sk-ant-oat01-…
anthropic-beta: oauth-2025-04-20             # REQUIRED
Content-Type: application/json
User-Agent: ClaudeStatus/<version>
```

Account-level: aggregates usage from every device/surface (claude.ai, desktop,
mobile, Claude Code) — no local JSONL parsing needed.

## Response — VERIFIED against a live 200 on 2026-09-04

Recorded verbatim (pretty-printed only) as
`tests/ClaudeStatus.Core.Tests/Fixtures/usage-normal.json`. The shape below is
what the endpoint actually returns today, and it has **drifted from the original
spike notes** — see "Schema drift" at the end of this section.

```jsonc
{
  // Legacy flat keys. five_hour/seven_day are still populated; every other
  // flat bucket was null on this account. Each is an object with SIX fields now.
  "five_hour": {
    "utilization": 29.0,
    "resets_at": "2026-09-04T15:39:59.968246+00:00",
    "limit_dollars": null, "used_dollars": null,
    "remaining_dollars": null, "locked_reason": null
  },
  "seven_day": { /* same shape */ },

  // Null on this account, but they exist as keys. Note the CODENAMED buckets:
  "seven_day_oauth_apps": null, "seven_day_opus": null, "seven_day_sonnet": null,
  "seven_day_cowork": null, "seven_day_omelette": null,
  "tangelo": null, "iguana_necktie": null, "omelette_promotional": null,
  "nimbus_quill": { "utilization": 0.0, "resets_at": null, /* ... */ },
  "cinder_cove": null, "amber_ladder": null, "juniper_tide": null,

  // Current structured data — THIS is what we parse.
  "limits": [
    { "kind": "session",       "group": "session", "percent": 29,
      "severity": "normal",  "resets_at": "...", "scope": null, "is_active": false },
    { "kind": "weekly_all",    "group": "weekly",  "percent": 58,
      "severity": "normal",  "resets_at": "...", "scope": null, "is_active": false },
    { "kind": "weekly_scoped", "group": "weekly",  "percent": 88,
      "severity": "warning", "resets_at": "...", "is_active": true,
      "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
  ],

  "extra_usage": { "is_enabled": false, "monthly_limit": null, "used_credits": null,
                   "utilization": null, "currency": null, "decimal_places": null,
                   "disabled_reason": null, "user_disabled": true,
                   "spend_limit_reached": false, "credits_ever_enabled": true,
                   "daily": null, "weekly": null },

  "spend": { "used": { "amount_minor": 0, "currency": "USD", "exponent": 2 },
             "limit": null, "percent": 0, "severity": "normal", "enabled": false,
             "disabled_reason": null, "cap": null, "balance": null,
             "auto_reload": null, "disclaimer": "<markdown string>",
             "can_purchase_credits": false, "can_toggle": false },

  "member_dashboard_available": false
}
```

### Schema drift since the original spike notes

Everything here was wrong or missing in the first draft of this document. It is
the clearest possible argument for tolerant parsing:

| Change | Impact on us |
|---|---|
| `limits[]` entries gained `group` and `is_active` | Ignore both for now; `is_active` may later be a better "which window is binding" signal than comparing percentages. |
| `scope.model` gained `id`; `scope` gained `surface` | Match on `display_name`, tolerate `id: null`. |
| Flat window objects gained `limit_dollars`, `used_dollars`, `remaining_dollars`, `locked_reason` | Unused. `locked_reason` is worth watching — likely how a hard lock surfaces. |
| New codenamed top-level buckets (`tangelo`, `iguana_necktie`, `nimbus_quill`, `cinder_cove`, `amber_ladder`, `juniper_tide`, `omelette_promotional`, `seven_day_omelette`) | Unreleased-feature placeholders. **Never enumerate top-level keys**; read only the ones we know. |
| New top-level `spend` object and `member_dashboard_available` | Unused in Iteration 1. |
| `extra_usage` grew from 4 fields to 12 | Unused. |
| `percent` came back as an **integer** (29, 58, 88) while `utilization` was a **float** (29.0) | Parse both as `double`. Do not assume a decimal point. |
| `severity` observed: `"normal"`, `"warning"` | Treat as an open string set. Do not deserialize into a closed enum. |

Mapping to our domain (unchanged):

| `limits[].kind` | `scope.model.display_name` | `UsageSnapshot` field |
|---|---|---|
| `session` | — | `Session` (do not assume 5 h; use `resets_at`) |
| `weekly_all` | — | `Week` |
| `weekly_scoped` | `Fable` | `WeekFable` |
| `weekly_scoped` | other (Opus, Sonnet…) | keep in `OtherWindows` list; unknown names must not break parsing |

Parsing rules: tolerate unknown keys, unknown `kind`s, unknown `severity`
values, nulls (including `resets_at: null`), and a missing `limits` array (fall
back to the flat keys). Percentages are 0–100 and may be int or float.

### Response headers — do not record these

A successful response carries `anthropic-organization-id` and
`anthropic-workspace-id`. They are account identifiers. Never log them, never
put them in a fixture, never include them in a bug report.

## Credential

`accessToken` comes from Claude Code's OAuth login:

| OS | Location |
|---|---|
| Windows | `%USERPROFILE%\.claude\.credentials.json` |
| Linux | `~/.claude/.credentials.json` |
| macOS | Keychain item **`Claude Code-credentials`** (file may not exist) |
| any | `$CLAUDE_CONFIG_DIR/.credentials.json` overrides when set |

```json
{ "claudeAiOauth": {
    "accessToken": "sk-ant-oat01-…",     // 108 chars on the account we inspected
    "refreshToken": "sk-ant-ort01-…",
    "expiresAt": 1770412938485,          // ms since epoch
    "refreshTokenExpiresAt": 1772... ,   // ms since epoch - NOT in the original notes
    "scopes": ["user:file_upload","user:inference","user:mcp_servers",
               "user:profile","user:sessions:claude_code"],
    "subscriptionType": "max",
    "rateLimitTier": "default_claude_max_5x" } }
```

- Access token expires **roughly hourly**. 401/403 ⇒ expired or revoked.
- Refresh: ccusage refreshes with the `refreshToken` and **writes the rotated
  pair back** to `.credentials.json` so Claude Code stays in sync. See the
  dedicated section below.
- ⚠ Refresh tokens **rotate**: if we refresh and don't write back, Claude Code
  breaks; if Claude Code refreshes and we hold a stale copy, we break.

### OAuth refresh endpoint (researched 2026-09-04 — NOT executed)

**We deliberately did not perform a refresh.** Refresh tokens rotate, so a test
refresh would have invalidated the user's live Claude Code login. Everything below
is from published sources plus a harmless unauthenticated probe. Treat the
parameter list as *high confidence*, and the exact success-response shape as
*unverified* until Phase 6 actually implements this.

```
POST https://platform.claude.com/v1/oauth/token
Content-Type: application/json

{ "grant_type": "refresh_token",
  "refresh_token": "sk-ant-ort01-…",
  "client_id": "9d1c250a-e61b-44d9-88ed-5944d1962f5e" }
```

| Item | Value | Confidence |
|---|---|---|
| Token endpoint (current) | `https://platform.claude.com/v1/oauth/token` | **Verified live** — POST returns 400 `Unsupported grant_type: None` for `{}`, so it exists and parses JSON `grant_type`. |
| Token endpoint (legacy) | `https://console.anthropic.com/v1/oauth/token` | **Verified live** — behaves identically, same error, same `request_id` format. It is *not* dead, contrary to some issue reports. |
| `client_id` | `9d1c250a-e61b-44d9-88ed-5944d1962f5e` | High — public, appears in every community client. |
| Body encoding | JSON, **not** `x-www-form-urlencoded` | High — the 400 above proves JSON `grant_type` is parsed. |
| Authorization endpoint | `https://claude.ai/oauth/authorize` (PKCE, S256) | Medium — not needed by us; we never run a login flow. |
| Success response shape | presumably `access_token` / `refresh_token` / `expires_in` | **Unverified.** |
| `User-Agent` | Do **not** send a `claude-code/`-prefixed UA to the token endpoint | Medium — community reports of rate limiting when impersonating the CLI. We send `ClaudeStatus/<version>`, which sidesteps this. |

Note the scopes in a real credentials file today
(`user:file_upload user:inference user:mcp_servers user:profile user:sessions:claude_code`)
differ from the older published set (`org:create_api_key user:profile user:inference`).
We request nothing, so this only matters if a future scope removal breaks the
usage endpoint.

⚠ **Refresh stays out of Iteration 1.** Option A (live read, never refresh) is
the plan. If Phase 6 adds it, the write-back to `.credentials.json` must be
atomic (temp file + rename), must preserve every field we do not understand, and
must cope with Claude Code refreshing concurrently.

### Decision: how ClaudeStatus obtains the token

Options, ordered by how little secret material we hold ourselves:

| Option | We store | Works when Claude Code not running | Risk |
|---|---|---|---|
| **A. Live read** of Claude Code's store on every poll (file / Keychain) | nothing | only until the token expires (~1 h) then stale | zero credential storage; needs user consent + Keychain prompt on macOS |
| **B. Live read + refresh & write-back** (ccusage model) | nothing persistent | yes | must implement refresh correctly and write back atomically; concurrent writers |
| **C. Paste token in Config, we store it encrypted** | access + refresh token | yes, until rotation desync | our secret store becomes the primary target; desync with Claude Code |

**Recommended for Iteration 1: A, with B as Phase 6 hardening.** It satisfies
the golden rule best (we never persist a token), and stale-after-1h is an
honest UX ("Open Claude Code to refresh your login"). Option C is only a
fallback for users without Claude Code installed and should stay behind an
"advanced" toggle. Config UI therefore becomes: "Source: Claude Code login
(recommended) / Manual token (advanced)".

## Rate limiting — the real operational risk

The endpoint 429s aggressively and can stay throttled for the whole session
(`retry-after: 0`, sometimes no header). Known-good behaviour:

**Measured 2026-09-04.** An **unauthenticated** request (missing/empty Bearer)
returns `429 rate_limit_error` with `Retry-After: 1731` — **not** 401. An
authenticated request from the same IP seconds later returned 200, so the
unauthenticated and authenticated buckets are separate.

> ⚠ **Consequence for our error handling:** a 429 does **not** imply "slow down".
> It may equally mean "your token is missing or malformed". Never treat 429 alone
> as proof the credential is fine, and never treat it as proof we are polling too
> fast. Distinguish by whether we actually had a token to send.

`Retry-After` **is** present (1731 s here), contradicting the earlier note that
it is often absent or 0. Honour it when present, capping at our own maximum.

- Poll at **60 s** while succeeding; on 429/error back off exponentially to a
  **300 s** cap; snap back to base on the next success.
- Never fire on every UI repaint; a manual "Refresh" button must be
  rate-limited client-side (e.g. min 30 s between forced calls).
- Show last-known values with a "stale (Xm)" badge instead of blanking.
- Cache the last snapshot on disk (no secrets in it) for instant startup.

## Secondary sources (not used in Iteration 1)

- Response headers on every Messages API call made by Claude Code:
  `anthropic-ratelimit-unified-{claim}-utilization`, `-reset`, `-status`.
  Only available if we make model calls — we won't.
- Other endpoints seen in `cli.js`: `/api/oauth/profile`,
  `/api/oauth/account/settings` (could give plan name for the Info window).
- Local `~/.claude/stats-cache.json` (token counts) — offline estimation only.

## Known caveats to surface in the UI

- Numbers can differ between Claude Code `/usage` and claude.ai Settings > Usage
  at the same instant. We show the endpoint's value and name the source.
- Unofficial: a banner in Info and README.

## Fixtures

Recorded responses live in `tests/ClaudeStatus.Core.Tests/Fixtures/` — see the
README there. `usage-normal.json` is the byte-faithful 2026-09-04 capture.

## Sources

Verified in-repo on 2026-09-04:
- Live `GET /api/oauth/usage` → 200, recorded as `usage-normal.json`.
- Live unauthenticated `GET /api/oauth/usage` → 429 + `Retry-After: 1731`.
- Live `POST` probes of both token endpoints → 400 `Unsupported grant_type: None`.

External:
- cedws gist "Anthropic API notes for the Claude Code OAuth flow" — client_id,
  endpoints, PKCE, refresh params:
  https://gist.github.com/cedws/3a24b2c7569bb610e24aa90dd217d9f2
- ccusage (wakamex) README — endpoint, headers, response, credentials, refresh, thresholds
- fschmutz/claude-usage-panel — `limits[]` schema (kind/percent/severity/resets_at/scope.model)
- bozdemir/claude-usage-widget — adaptive 60→300 s polling rationale
- dennykim123/claude-codex-battery — macOS Keychain item name
- anthropics/claude-code issues #30930, #31021, #31637 — 429 behaviour
- Maciek-roboblog/Claude-Code-Usage-Monitor issue #202 — endpoint as authoritative source

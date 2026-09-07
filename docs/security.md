# Security — threat model and handling rules

Version 2, 2026-09-04. Written in Phase 1; §7 extended in Phase 2; §3 and §6
reviewed and signed off in Phase 3. Update this document **in the same commit** as
any change to how credentials are obtained, stored, or transmitted.

The normative rules are `docs/manual.md` §8 and §4 of this document. This document explains *why* they
are what they are, and gives the checklist to review against.

---

## 1. What we are protecting

One asset, and it is the whole story: **the Claude OAuth access token**
(`sk-ant-oat01-…`) and its **refresh token** (`sk-ant-ort01-…`).

An attacker holding these can impersonate the user against Anthropic's API:
spend their subscription quota, read account metadata, and — with the refresh
token — keep doing so indefinitely, because refreshing rotates the pair and locks
the legitimate user out of their own Claude Code session.

The refresh token is strictly the more valuable of the two. The access token
expires in about an hour; the refresh token has its own, much later
`refreshTokenExpiresAt`.

Nothing else in this app is sensitive. Usage percentages, reset times and
settings are not secrets.

## 2. Design decision that removes most of the risk

**Iteration 1 stores no token at all.**

We read Claude Code's existing login at poll time, use the access token for one
HTTPS request, and let it go. There is no ClaudeStatus credential store to
attack, no encrypted blob to exfiltrate, no key to steal, and no rotation
desync to cause.

The cost is honest and small: about an hour after Claude Code last refreshed,
our data goes stale until the user opens Claude Code again. The UI says so
plainly rather than pretending.

Manual token entry (Option C in `docs/data-source.md`) is an **advanced
fallback** for users without Claude Code installed. It is the only path that
creates a stored secret, and everything in §4 exists for it.

**We never call the refresh endpoint in Iteration 1.** Refreshing rotates the
pair; if we rotate and fail to write back atomically, we break the user's
Claude Code login. That is a worse outcome than stale numbers.

## 3. Threat model

| # | Threat | Mitigation | Status |
|---|---|---|---|
| T1 | Token written to a log file | `RedactingLoggerProvider` wraps the file provider as the outermost provider, so no log call can reach the file unscrubbed. Never interpolate a secret into a log message in the first place — redaction is the second line of defence, not the first. | Phase 6 ✅ |
| T2 | Token in an exception message or crash report | Never construct an exception whose message contains the secret. No crash reporting or telemetry ships at all. | Ongoing |
| T3 | Token in a screenshot or the UI | Credential entry is a password box. Nothing else ever displays it. Not shown in Info, Config, or the details window. | Phase 5 |
| T4 | Token committed to git | `.gitignore` excludes `.credentials.json`, `*.local.json`, `secrets.json`, `.env*`, `*.pfx`, `*.p12`, `*.snk`. Fixtures are scanned before being recorded. | Phase 0 ✅ |
| T5 | Token lingering in managed memory | Handle the raw secret as `byte[]`, zero it with `CryptographicOperations.ZeroMemory` after use. Never a `string` beyond the input control (strings are immutable and stay in the heap until GC, possibly into a page file or crash dump). | Phase 3 ✅ |
| T6 | Config folder copied to another machine or user | OS-bound encryption by construction (§4). A copied config is inert. This is a *design requirement*, not a nice-to-have. | Phase 3 ✅ |
| T7 | Token sent somewhere it should not go | Exactly one host: `api.anthropic.com`, HTTPS, default certificate validation. No proxies, no redirect-following to other hosts, no telemetry, no diagnostics upload. | Phase 2 ✅ |
| T8 | Rotation desync breaking Claude Code | We do not refresh. If Phase 6 ever adds it: atomic write (temp file + rename), preserve unknown fields, tolerate concurrent writers. | Deferred |
| T9 | Local malware reading the credential | **Out of scope.** Anything running as the user can read Claude Code's own credential file directly. We do not make this worse; we cannot make it better. | Accepted |
| T10 | Account identifiers leaking | Responses carry `anthropic-organization-id` and `anthropic-workspace-id` headers. Never log them, never record them in fixtures, never include them in a bug report. | Ongoing |
| T11 | Linux without a keyring | Falls back to an AES-256-GCM key file at `chmod 600`. Weaker than a keyring, so the Config window must show a **visible warning** when this path is active. | Phase 3 ✅ (warning renders in Phase 5) |

### Explicitly out of scope

- An attacker with code execution as the user (T9).
- Physical access to an unlocked machine.
- A malicious Anthropic endpoint. We trust `api.anthropic.com`.
- Side-channel attacks on the OS secret stores themselves.

## 4. Storage mechanism per OS (Option C only)

Nothing here runs unless the user opts into manual token entry.

| OS | Mechanism | Bound to |
|---|---|---|
| Windows | `ProtectedData` (DPAPI), `DataProtectionScope.CurrentUser`, plus a per-install random 32-byte entropy file in the config dir | user **and** machine | 
| macOS | Keychain via `Security.framework` (`SecItemAdd` / `SecItemCopyMatching`), access group scoped to the app | user login keychain |
| Linux | Secret Service (libsecret) over D-Bus; if unavailable, AES-256-GCM with a `chmod 600` key file in `$XDG_CONFIG_HOME/claudestatus/` **and a visible warning** | user session keyring, or file permissions |

Rules that admit no exceptions:

- **Never roll our own crypto.** No custom KDFs, no XOR, no "obfuscation".
- **Never derive a key from a machine name, MAC address, username, or any other
  guessable value.** That is not encryption, it is encoding.
- **Never hardcode a key, salt, or IV.** The DPAPI entropy is random per install
  and lives beside the config, not in the binary.
- The config JSON stores `HasCredential: true|false` and **no secret bytes**.

### 4.1 The free-text fields in settings.json

`AppSettings` deliberately had **no `string` properties at all**, enforced by a
reflection test (`ConfigStoreTests.AppSettings_declares_no_unreviewed_property_that_could_hold_a_secret`).
The rule existed so that no code path could ever put a credential in
`settings.json`, even by accident.

Three exceptions were made on 2026-09-05, all for the same reason: enumerating
the shipped values instead would make anything added as a loose file
unselectable, which defeats the point of those mechanisms.

| Field | Holds | Bound by |
|---|---|---|
| `LanguageTag` | A BCP-47 tag, e.g. `es`, `pt-BR` | ≤ 12 chars, `^[a-z]{2,3}(-[A-Za-z0-9]{2,4})?$` |
| `ThemeId` | A theme id, e.g. `nebula` | ≤ 32 chars, lower-case ASCII letters/digits/`-`/`_`, starting with a letter |
| `FontFamily` | A font stack, e.g. `Segoe UI, Ubuntu` | ≤ 64 chars, letters, digits, space, `,`, `-`, `.`, `'` — **and** rejected if `Redactor.LooksRedacted` says it is credential-shaped |

What makes each exception safe is that the field is **structurally incapable of
holding a token**. `AppSettings.Normalized()` discards anything that does not
match, replacing it with the default.

An access token is ~108 characters and contains `-` runs and mixed case in
positions the first two patterns reject; a refresh token likewise. `FontFamily`
is the loosest of the three — free text is genuinely expected there — so it
carries the extra redaction check on top of the length cap. The cap alone already
prevents a whole token from fitting; the check is belt and braces.

`ThemeId` also becomes a path segment under `themes/`, so its validation is what
stops a path escaping that folder as well as what keeps a credential out.

`ConfigStoreTests` asserts all three directly with token-shaped and
traversal-shaped inputs, and asserts that a hostile value never survives a real
`SaveAsync` round trip.

**If you add another string property to `AppSettings`,** add it to the
allowlist in that test *and* give it a normalizer with the same property: that a
credential cannot pass through it. A widened allowlist without a normalizer
silently removes this control.

Translation and theme files (`lang/*.json`, `themes/*.json`, `Strings*.resx`) are
shipped or user-supplied, world-readable content and must never contain
credential material; a test scans every resource value with
`Redactor.LooksRedacted`. Theme files are parsed as untrusted input: size-capped,
name-validated against path traversal, and skipped entirely when malformed.

## 5. Transport

- One `HttpClient` from `IHttpClientFactory`. 15 s timeout.
- HTTPS only, default certificate validation. No pinning (it breaks on
  legitimate rotation and buys little against T9).
- `AllowAutoRedirect` disabled, or redirects restricted to the same host — a
  redirect must never carry the `Authorization` header to another origin.
- `User-Agent: ClaudeStatus/<version>`. Do **not** impersonate `claude-code/…`;
  community reports link that to rate limiting on the token endpoint.
- No proxy configuration is read from the environment for this request.

## 6. Review checklist

Run through this before merging anything that touches credentials, and at the
end of Phase 3 and Phase 6.

**Last run: 2026-09-04, end of Phase 3. Every item that can be checked yet passes.**

- [x] No `string` holds the raw secret beyond the input control.
      *One deliberate exception, §7.4: the `Encoding.UTF8.GetString` inside
      `ClaudeSubscriptionUsageProvider`, which is the HTTP header boundary. It is
      the only `GetString` on a secret path in the whole of `src/`.*
- [x] Every `byte[]` holding secret material is zeroed with
      `CryptographicOperations.ZeroMemory` on every path, including exceptions.
      *`AccessTokenLease` makes this structural; `CredentialService.StoreAsync`
      wipes in a `finally`, including for a rejected token; the credentials-file
      reader wipes the whole file buffer, which also contains the refresh token.*
- [x] No log statement, exception message, or assertion message can contain the
      secret, even under `LogLevel.Trace`.
      *Provider failures log the status code only, never the body. Parse failures
      carry no body. Credential rejections log the problem enum, not the value.*
- [x] The redaction filter is registered globally and has tests proving it
      catches `sk-ant-oat01-…`, `sk-ant-ort01-…`, `Bearer …`, JWT shapes, secret
      JSON fields and account-identifier headers.
      *`RedactingLoggerProvider` is the outermost provider, wrapping
      `RollingFileLoggerProvider`, so there is no path from a log call to the
      file that skips it. End-to-end tests write to a real file and assert
      nothing credential-shaped lands in it. Confirmed against a real run of the
      app on 2026-09-04.*
- [x] A grep for live `sk-ant-oat01-` / `sk-ant-ort01-` values over the working
      tree returns only synthetic test material.
- [x] Config JSON contains no secret bytes.
      *Enforced by a test asserting `AppSettings` declares no `string` property.*
- [x] Copying the config dir leaves the app unable to decrypt.
      *Automated for Windows (DPAPI ciphertext moved next to different entropy)
      and for the Linux AES-GCM fallback. Still needs manual cross-machine
      confirmation per OS at Phase 5 QA — the single-box test is a good proxy,
      not a proof.*
- [x] Only `api.anthropic.com` appears as an HTTP destination in the codebase.
      *Verified by grep over `src/` and by a test asserting the request URI and host.*
- [x] Fixtures scanned for credential material before being committed.
- [ ] The Linux keyring-unavailable warning actually renders.
      *Blocked until Phase 5 builds the Config window. `ISecretStore.IsHardened`
      and `CredentialService.IsStoreHardened` carry the signal, and a test pins
      which stores report which value.*

Also verified this round:

- [x] No hardcoded key, salt, or IV anywhere in `src/`. Every key, nonce and
      entropy value comes from `RandomNumberGenerator`.
- [x] No key is derived from a machine name, user name, or anything else
      guessable. *A test asserts two install directories get different DPAPI
      entropy.*
- [x] A 429 is never treated as evidence the credential is valid.
      *`CredentialTestOutcome.Inconclusive`, with a test.*
- [x] Every `string` property on `AppSettings` is on the reviewed allowlist and
      has a normalizer that a credential cannot survive. *§4.1. Three properties:
      `LanguageTag`, `ThemeId` and `FontFamily`, each with token-shaped and
      traversal-shaped inputs tested.*
- [x] No shipped resource or translation file contains credential-shaped text.
      *`ResourceFileTests.No_resource_value_looks_like_a_credential` scans all
      five `.resx` files with `Redactor.LooksRedacted`.*
- [ ] A loose `lang/*.json` or `themes/*.json` file supplied by a third party is treated as
      untrusted input. *Parsing is hardened and tested (size cap, tag validation
      against path traversal, malformed content ignored), but no adversarial
      review of a hostile translation file has been done — worst case is
      misleading text in the interface, not code execution.*

## 7. Known accepted weaknesses

1. **Live-read requires reading Claude Code's credential file.** On macOS this
   triggers a Keychain prompt, which is correct and should not be suppressed.
2. **The Linux file fallback (T11)** protects against a copied config folder but
   not against another process running as the same user. Warned in the UI.
3. **The endpoint is unofficial.** It can change or vanish. This is a
   reliability risk, not a security one, but it is why we never treat a failure
   as evidence the credential is bad.
4. **The token becomes a `string` at the HTTP boundary.** `HttpClient`'s
   header API takes a string, and there is no byte-oriented path through
   `HttpRequestHeaders`. `ClaudeSubscriptionUsageProvider` therefore creates one
   inline, hands it straight to the header, and never stores it in a local,
   field, or log; the `byte[]` lease is still zeroed. This partially weakens T5
   for the duration of one request. Accepted: the alternative is a hand-written
   HTTP client, which would be far worse for security overall.
5. **429 does not mean what it appears to mean.** An unauthenticated request
   returns `429 rate_limit_error`, not 401 (measured 2026-09-04). Never use a
   429 alone to conclude the credential is valid.

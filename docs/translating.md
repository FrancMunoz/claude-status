# Translating ClaudeStatus

ClaudeStatus ships in English, Spanish, Catalan, German and French. Adding a
language, or fixing a wording in one that exists, does not require the .NET SDK
or a rebuild.

There are two ways in. Pick the first one that applies to you.

---

## 1. You just want to add or fix a language on your own machine

Copy a translation file into the app's `lang` folder, translate the values, and
restart. The language appears in **Config → Language**.

The folder is shown in the Config window under the language picker. It is:

| OS | Folder |
|---|---|
| Windows | `%APPDATA%\ClaudeStatus\lang\` |
| macOS | `~/Library/Application Support/ClaudeStatus/lang/` |
| Linux | `$XDG_CONFIG_HOME/claudestatus/lang/` (usually `~/.config/claudestatus/lang/`) |

Create it if it is not there. Name the file after the language tag — `it.json`
for Italian, `pt-BR.json` for Brazilian Portuguese — and put a flat object of
key/value pairs in it:

```json
{
  "Details_Heading": "Utilizzo di Claude",
  "Tray_Refresh": "Aggiorna",
  "Tray_Quit": "Esci"
}
```

Two things worth knowing:

- **You do not have to translate everything.** Any key you leave out falls back
  to the built-in text for that language, and then to English. A file with three
  lines in it overrides exactly those three.
- **A file for an existing language overrides it.** Dropping in `es.json` lets
  you correct the shipped Spanish without touching the app.

Start from `src/ClaudeStatus.Core/Localization/Strings.resx` in the repository
for the full list of keys and the English text.

## 2. You want your language shipped with the app

Copy `src/ClaudeStatus.Core/Localization/Strings.resx` to
`Strings.<tag>.resx`, translate every `<value>`, and open a pull request. Also
add the tag to `LanguageCatalog.BuiltIn` so it appears in the picker, and add
the file name to the theory cases in `ResourceFileTests`.

The build produces a satellite assembly per language automatically; there is
nothing to register in a project file.

---

## Rules that the tests enforce

`dotnet test` fails if any of these is broken, so you will find out before a
reviewer does:

1. **Every language has exactly the same keys as English.** A key added to one
   file and forgotten in another is an error, not a silent fallback.
2. **Placeholders must match.** If English has `Details_Status_LastUpdated` =
   `"Last updated {0} ago."`, your translation must use `{0}` exactly once. A
   dropped placeholder loses the number; an invented `{1}` produces a broken
   string on screen.
3. **No value may be empty.** An untranslated string is fine; a blank one makes
   the control disappear.
4. **No value may look like a credential.** Resource files are shipped and
   world-readable.
5. **Every key must be used, and every key used must exist.** A binding to a key
   that does not exist shows the key itself in the interface.

## Writing good translations here

- **Keep the placeholder, move it freely.** `{0}` can go anywhere in the
  sentence, and often has to. German puts the time first: "Vor {0} aktualisiert."
- **The fragments are separate keys.** `Age_HoursMinutes` produces `2h 5m`, which
  is then embedded in `Details_Status_LastUpdated`. If your language needs the
  fragment inflected, rewrite the surrounding sentence so it does not have to be.
- **Number formatting is automatic.** `Bar_Percent` is `{0:0.#} %`, and Spanish
  renders that as `43,5 %` with no extra work. Do not hard-code a separator.
- **Do not translate the brand.** "ClaudeStatus", "Claude Code", "Fable",
  "Anthropic", "DPAPI", "LaunchAgent", "AppIndicator" stay as they are. The
  language names in the picker come from the operating system, not from these
  files.
- **Language names are automatic too.** A language added by dropping in a file
  gets its own native name in the picker from the OS culture data.

## Key naming

Keys are `Area_Thing`, where the area says where it appears:

| Prefix | Where it shows |
|---|---|
| `Tray_` | Tray icon menu and hover tooltip |
| `Details_` | The details popup (left click) |
| `Bar_`, `Reset_`, `Age_` | Fragments inside the details popup |
| `Config_` | The configuration window |
| `Info_` | The about window |
| `Credential_`, `CredentialTest_` | Messages after entering or testing a token |
| `Store_`, `Autostart_`, `TokenSource_`, `Provider_` | Names of platform mechanisms |
| `Common_` | Shared fragments — a dash, "n/a" |

Anything that is not shaped like this will not be found by the key-usage test,
so keep to it.

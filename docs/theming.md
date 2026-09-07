# Theming ClaudeStatus

Eleven themes ship with the app. Adding your own needs a text editor and no
rebuild — the same arrangement as translations.

**Config → Theme** holds all of it: the theme, the interface font, and the popup's
background transparency. Everything applies as you pick it, so the picker is its own
preview.

---

## The themes that ship

| Id | Name | |
|---|---|---|
| `light` / `dark` | Light, Dark | Neutral, blue accent |
| `claude` | Claude | Ivory paper and coral, after Anthropic's own palette |
| `ember` | Ember | Orange on near-black brown |
| `ocean` | Ocean | Azure on deep navy |
| `aurora` | Aurora | Teal-green on near-black; the brightest accent of the set |
| `nebula` | Nebula | Violet on aubergine |
| `orchid` | Orchid | Magenta-pink on charcoal |
| `sakura` | Sakura | Deep pink on near-white |
| `solar` | Solar | Amber on cream |
| `matcha` | Matcha | Green on off-white |

**Follow the system** is the default: it uses Light when the desktop is in light
mode and Dark when it is in dark mode.

## Adding your own

Put a JSON file in the `themes` folder inside the config directory. The folder is
shown in the Config window under the theme picker; it is:

| OS | Folder |
|---|---|
| Windows | `%APPDATA%\ClaudeStatus\themes\` |
| macOS | `~/Library/Application Support/ClaudeStatus/themes/` |
| Linux | `$XDG_CONFIG_HOME/claudestatus/themes/` |

**The file name is the theme's id.** `midnight.json` becomes the theme
`midnight`. Ids must be lower-case ASCII letters, digits, `-` and `_`, starting
with a letter.

```json
{
  "name": "Midnight",
  "primary":    "#7AA2F7",
  "background": "#1A1B26",
  "text":       "#C0CAF5",
  "alert":      "#F7768E"
}
```

All four colours are required — a file missing one is skipped entirely rather
than half-applied, because a half-applied theme looks like a rendering bug rather
than a bad file. `#RGB` and `#RRGGBB` both work, with or without the `#`.

**A file named after a built-in replaces it**, keeping its position in the list.
That is how you correct the shipped Dark theme without forking the app: save your
version as `dark.json`.

Restart the app, or just reopen Config — the list is rebuilt each time the window
opens.

## What the four colours do

| | |
|---|---|
| `primary` | Headings, and the accent for buttons, sliders, checkboxes and the combo box |
| `background` | The window surface |
| `text` | Body text |
| `alert` | Bars over your threshold, warning banners, and severity labels the endpoint calls anything but normal |

**Everything else is derived**, and deliberately not yours to set:

- the **muted grey** for captions is your text colour pulled 45 % toward your
  background, so it tints with the palette and cannot be picked against the wrong
  backdrop;
- **card and border** colours are the background nudged toward the text;
- the **warning panel** is the background tinted with your alert colour;
- **light or dark** is read from the background's luminance, which also selects
  the light or dark variant of the built-in control theme.

Four colours instead of nine is a deliberate constraint. The derived values
cannot be internally inconsistent, which is the failure mode a longer list
invites.

## Making one that works

The built-in themes are all held to these, and yours should be too:

- **Body text at 4.5:1 against the background**, and the derived grey at 3:1 —
  the WCAG thresholds. A palette that fails these is not a theme, however good
  the swatch looks.
- **`alert` clearly different from `primary`.** Not merely darker: a contrast
  check will pass two pinks a few degrees apart, and you will not be able to tell
  an over-threshold bar from a heading. Sakura's alert was originally a crimson
  beside its pink primary and had to be pulled toward orange-red.
- **`primary` and `alert` at 3:1 against the background.** A warning that reads
  as a subtle hint is not a warning.

Contrast is easy to check: any online WCAG contrast checker takes two hex values.

## Font

The picker offers a handful of families that ship broadly and read well at 11 px,
plus **Custom…** with a text box for anything else — type `JetBrains Mono` and it
will be used if installed. A font that is not installed falls back to the system
default silently.

Family names are capped at 64 characters and restricted to the characters real
font names use.

## Popup transparency

Applies to the small details popup only, and never to its text — a translucent
number is a number you have to squint at. The other windows are always opaque:
they are for reading, not for glancing past.

**10 % is the default** — enough that the popup reads as an overlay rather than a
window, little enough that nothing behind it competes with the numbers. **0 % is
a solid panel**, and stays solid: it is a real choice, not the absence of one, so
picking it survives a reload. At **100 %** the panel and its border vanish
entirely and the readings float over the desktop, which is what an on-screen
display traditionally looks like. The text stays fully legible and the buttons
stay clickable at every setting, because only the background ever carries the
alpha.

Some Linux compositors do not grant window transparency. Where that happens the
popup is simply opaque and nothing else changes.

## What is not themed

**The tray icon.** It sits on a taskbar this app does not control, and it keeps
its own contrast logic — light ink on a dark taskbar, dark on a light one, and
the alert colour when you are over threshold. A themed icon would be invisible on
half of all systems, which is a bug this project has already shipped once.

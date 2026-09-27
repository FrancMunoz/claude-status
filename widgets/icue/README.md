# iCUE widgets

Widgets for CORSAIR devices that show the ClaudeStatus reading. They are web
pages iCUE runs on the device's screen; ClaudeStatus serves them the numbers
over a local port when **Config → Behaviour → Share the reading with the iCUE
widget** is on.

| Folder | Device | Slot |
|---|---|---|
| `ClaudeUsage/` | XENEON EDGE (`dashboard_lcd`) | Small horizontal, 840×344 |

Everything about installing, the data contract, developing and packaging is in
[`docs/icue-widget.md`](../../docs/icue-widget.md). In short:

```
npm install -g icuewidget-cli@0.4.47
icuewidget validate widgets/icue/ClaudeUsage
icuewidget package  widgets/icue/ClaudeUsage
```

The package the CLI writes here (`*.icuewidget`) is git-ignored; releases
attach the one CI builds.

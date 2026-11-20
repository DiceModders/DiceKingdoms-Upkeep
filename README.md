# DiceKingdoms Upkeep

A lobby rule for Dice Kingdoms (off by default): defence buildings cost something to keep.

- **Guard towers** eat food every turn (1 each, set in the lobby).
- **Lighthouses** use wood or stone every turn (1 each, whichever you have more of).
- An unpaid guard tower or lighthouse gets the game's hungry state and **does not shoot** in that turn's combat. An unpaid lighthouse also gives **no fish bonus** at the next harvest.
- **Cathedral offering:** during the build phase you can make an offering (4 gold per Cathedral, set in the lobby). It works like a market purchase: the gold goes at once, and those Cathedrals give double culture at the next harvest (the harvest screen and the tooltips show the doubled culture).

The upkeep is paid in the disasters phase, together with the game's own food upkeep for the houses.

## The Upkeep window

While a match with the rule on is running, a button on the top bar opens the Upkeep window:

- **Feed the guard towers** / **Pay the lighthouses**: switch off to keep the resources (those buildings then do not shoot).
- **Guard towers eat before the houses**: when food is short, the towers are fed first (otherwise the houses eat first).
- **Make an offering**: once per turn, in the build phase; the offering is sent like any of the game's own build actions.

A switch counts from the next turn, so every player's computer knows it before it is used. Computer players pay everything, and the houses eat first.

## Lobby rules

| Rule | Default |
| --- | --- |
| Upkeep | off |
| Food per guard tower | 1 |
| Wood or stone per lighthouse | 1 |
| Cathedral offering (gold) | 4 |

Every player in the match needs this mod (ModCore compares the mods in the lobby).

## Install

Needs BepInEx 6 (IL2CPP), [ModCore](https://github.com/DiceModders/ModCore) and [UiKit](https://github.com/DiceModders/UiKit). Put `DiceModders.Upkeep.dll` into `BepInEx\plugins`, with the game closed.

## Build

```
.\scripts\build.ps1 -GameDir "<your Dice-Kingdoms folder>"   # first time
.\scripts\build.ps1                                           # afterwards
```

The script builds the mod and copies it into the game's `BepInEx\plugins` folder (close the game first).

## Texts

`texts.json` holds the texts in English, German, French, Russian, Ukrainian and Chinese. A `DiceModders.Upkeep.texts.json` next to the DLL wins line by line.

## License

MIT

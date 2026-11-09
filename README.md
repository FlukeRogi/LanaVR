# LanaVR

**Play Planet of Lana in VR, on a giant floating 3D screen.**

LanaVR puts the game on a large screen in front of you while you stand on a small wooden platform on a lake, under a sky that changes with the game's own scenery. Each eye gets its own full picture, so the game has real depth, and moving your head shifts the view as if you were looking through a window into Lana's world.

The game itself plays exactly as normal, with a gamepad or keyboard.

## What you need

- **Planet of Lana** on Steam (the first game) on a Windows PC
- A **PC VR headset**, running through **SteamVR** or **Virtual Desktop**
- A **gamepad or keyboard**. Motion controllers aren't used.

## Installing

1. Go to **[Releases](../../releases)** and download the newest `LanaVR-<version>.zip`.
2. Unzip it anywhere. You'll get a `LanaVR` folder.
3. Double-click **`Install LanaVR.bat`** and type **1**.
4. Connect your headset, then start Planet of Lana from Steam.

The installer finds the game in your Steam library by itself. **The very first start takes a minute or two** while the mod sets itself up. After that, starting is quick.

If Windows Smart App Control blocks the mod's files, the game simply starts flat on your monitor.

## Playing

- **Put your headset on and play.** The screen appears in front of you once the scenery has faded in.
- **Recenter the screen:** click both thumbsticks together, or press **F8**. The screen also recenters by itself if you put the headset on after starting the game.
- **Step forward or back on the platform:** tap **Page Up** or **Page Down** to move your viewpoint 10 cm at a time.
- **VR settings:** open **Settings > VR**, from the main menu or the pause menu.

If no headset is found, the game plays normally on your monitor. If you connect the headset later, VR starts within about 30 seconds.

## VR settings

| Setting | What it does |
|---|---|
| Recenter view | Moves the screen in front of where you're looking |
| Screen size | How wide the screen is, from 1 to 10 m |
| Screen depth | How far away the screen is, from 1 to 10 m |
| Stereo strength | How strong the 3D effect is. 0 is flat. |
| Head tracking | How much moving your head shifts the view. 0 turns it off. |
| Sky quality | Low, Medium or High sharpness for the sky |
| Water height | Raises or lowers the lake |
| Ripple strength | How much the water moves. 0 is still water. |
| Screen reflection | Turns the screen's reflection in the lake on or off |
| Reflection quality | Low, Medium or High sharpness for that reflection |
| 3D scenery | The platform you stand on, plus the boat and poles around it |
| Scenery size | Makes the platform, boat and poles bigger or smaller |
| Cutscene borders | Shows the black bars the game adds during cutscenes |
| Reset to defaults | Puts every VR setting back to how it started |

The `README.txt` in the download describes each setting in more detail, and explains how to choose between SteamVR and Virtual Desktop if you have both.

## If something isn't right

- **The gamepad does nothing:** click the game's window on your monitor once, so Windows lets it have the controls.
- **The game stays flat:** make sure the headset is connected and SteamVR or Virtual Desktop is running, then restart the game.
- **Steam seems to do nothing when you press Play:** look for a Steam message waiting on your desktop. It can be hidden while you're in the headset.
- **The 3D effect feels uncomfortable:** lower **Stereo strength** in the VR settings.
- **The game starts to judder:** the mod keeps a short performance record in `BepInEx\LanaVR-performance.log` in the game folder. Please include it when reporting the problem.

## Removing the mod

Double-click **`Install LanaVR.bat`** again and type **2**. The game goes back to its original files, and your saves aren't touched.

## Good to know

- The VR settings page is in English. The rest of the game is unchanged.
- The platform, boat and poles are read from your own copy of the game while it runs, so the mod contains none of the game's art.
- The mod uses your saves and Steam achievements as normal.

## Credits and licences

- **[BepInEx](https://github.com/BepInEx/BepInEx)**, the mod loader, under the LGPL-2.1 licence
- **[Khronos OpenXR loader](https://github.com/KhronosGroup/OpenXR-SDK)**, under the Apache-2.0 licence

Full notices and licence texts come with the download, in `THIRD-PARTY-NOTICES.txt` and the `licenses` folder.

LanaVR is an unofficial fan mod. It isn't made or endorsed by Wishfully or Thunderful. Planet of Lana is their game, and you need your own copy to use this mod.

---

*Developers: how the mod works and how to build it is in [DEVELOPMENT.md](DEVELOPMENT.md).*

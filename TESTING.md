# Testing LanaVR

LanaVR plays Planet of Lana on a floating 3D screen in VR. You stand on a wooden platform on a lake, the screen floats in front of you, and each eye gets its own full render of the game. Thanks for helping to test it.

**This is a private test build, so please don't share the files or this page.** The skies in it are low-resolution samples until the final ones are ready, so don't judge the sky's sharpness, and they aren't licensed for use outside this test.

## What you need

- Planet of Lana on Steam (the first game), installed on a Windows PC
- A gamepad or keyboard. Motion controllers aren't used.
- A PC VR headset, using SteamVR or Virtual Desktop with VDXR

## Install

1. Open **Releases** on this page and download the newest `LanaVR-…-tester.zip`.
2. Unzip it anywhere.
3. Double-click `Install LanaVR.bat` and type `1`.
4. Connect your headset, then start the game from Steam. The first start takes a minute or two.

Windows Smart App Control can block unsigned mod files. If it does, the game just stays flat.

To remove the mod, double-click `Install LanaVR.bat` and type `2`. Your saves aren't touched.

## Controls

- **Recenter:** click both sticks together, or press F8
- **VR settings:** Settings > VR, from the main menu or the pause menu

The zip's `README.txt` explains every setting, and how to choose between SteamVR and VDXR.

## What to look at

No one has fully tested this in a real headset yet, so these matter most:

1. **Does it start?** Which headset and runtime did you use, and did the game appear in VR?
2. **Platform height:** does the deck feel level with your real floor, whether you're standing or seated?
3. **Scale:** do the deck, the boat and the poles feel the right size?
4. **Comfort:** any eye strain or discomfort? Try changing Stereo Strength, Screen Size and Screen Depth, and tell us which values felt best.
5. **Speed:** any stutter? Does it improve with 3D Scenery off in the VR settings?
6. **The VR menu:** do all the settings work with your gamepad?
7. **Cutscenes:** they should play with no black bars. The Cutscene Borders setting brings the bars back.

## Reporting a problem

Open **Issues** on this page and create a new issue. Please include:

- What happened, and what you expected
- Your headset, runtime and graphics card
- Where in the game it happened, if it matters
- The log file: `BepInEx\LogOutput.log` in the game's folder. Drag it into the issue. It can contain your Windows user name in file paths.

If the game plays flat instead of in VR, the lines in that log containing `[native]` usually explain why.

LanaVR is an unofficial fan mod, not made or endorsed by Wishfully or Thunderful.

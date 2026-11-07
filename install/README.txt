LanaVR 1.7: Planet of Lana on a floating 3D screen in VR
==========================================================

The game plays on a large virtual screen fixed in your room. Each eye gets its own
full render, so you see real depth. Moving your head changes the view, like looking
through a window into the game's world.

You need
  - Planet of Lana on Steam (the first game)
  - A gamepad or keyboard. Motion controllers are not used.
  - SteamVR, or Virtual Desktop with VDXR

Install
  1. Unzip this folder anywhere.
  2. Double-click "Install LanaVR.bat" and type 1.
  3. Start the game from Steam. The first start takes a minute or two.
  Windows Smart App Control can block unsigned mod files. If it does, the game just
  stays flat.

Use
  Connect your headset first, then start the game. If you connect later, VR starts
  within about 30 seconds. If no headset is found, the game plays normally on your monitor.

  Recenter:    click both sticks together, or press F8
  VR settings: Settings > VR, from the main menu or the pause menu

    Recenter view      Moves the screen in front of where you are looking
    Screen size        Width of the screen, 1 to 10 m
    Screen depth       Distance to the screen, 1 to 10 m
    Stereo strength    Depth effect. 1 matches your real eye separation, 0 is flat.
    Head tracking      How much head movement shifts the view. 1 is like a real window, 0 is off.
    Sky brightness     Brightness of the sky and water around the screen. 0 turns them off.
    Water height       Raises or lowers the lake, in metres from your floor. Raising it brings the screen's reflection closer.
    Ripple strength    How much the water moves. 0 is still water.
    3D scenery         The wooden platform you stand on, and the boat and poles around it. It is read from your
                       own copy of the game while it runs, so this mod contains none of that art.
    Cutscene borders   Shows the black bars the game adds above and below the picture during cutscenes.
    Reset to defaults  3 m wide, 2.5 m away, stereo 1, head tracking 1, sky brightness 1, water at -0.5 m,
                       ripple strength 1, 3D scenery on, cutscene borders off

Choosing SteamVR or Virtual Desktop
  By default the mod uses your system's active OpenXR runtime. To pick one, edit
  <game folder>\BepInEx\config\com.lanavr.planetoflana.cfg (it appears after the first start):

    Runtime = auto      the system's active runtime
    Runtime = steamvr   SteamVR
    Runtime = vdxr      Virtual Desktop VDXR
    Runtime = off       play flat, with no VR

  The same file has ResolutionScale, which lowers or raises the per-eye resolution
  if you need more speed or sharpness. It applies the next time the game starts.

Backdrop
  The sky around the screen changes with the game's background, fading across over 3 seconds.
  The images and the list saying which goes with which background are in
  <game folder>\BepInEx\plugins\LanaVR\skybox. To change them, edit skyboxes.txt there and add
  your own PNG or JPG images, in either of these formats:
    2:1 panorama (equirectangular), such as 8192x4096. The middle of the image faces forward.
    4:3 horizontal-cross cubemap, such as 8192x6144.
  4K to 8K wide looks best. Anything larger is reduced to 16K panorama detail. An image that
  can't be used leaves the current sky in place. Restart the game after editing skyboxes.txt.
  The same file has tints that shift the sky's colour along with the game's own colour fades.

  You stand on a platform at the height of your room's floor if your headset reports one, and
  otherwise 1.5 m below your head when you last recentered. The lake below it reflects this sky,
  the game screen and the scenery.

Troubleshooting
  - The game plays flat: check <game folder>\BepInEx\LogOutput.log for lines with "[native]".
  - Steam seems to do nothing when you press Play: look for a Steam dialog on your desktop
    waiting for an answer, which can be hidden while you are in the headset.
  - Far backgrounds feel uncomfortable: lower Stereo strength.
  - The VR page's text is English only. The rest of the game's text is unchanged.

Uninstall
  Double-click "Install LanaVR.bat" and type 2. Saves are not touched.

Third-party components are listed in THIRD-PARTY-NOTICES.txt.
LanaVR is an unofficial fan mod, not made or endorsed by Wishfully or Thunderful.

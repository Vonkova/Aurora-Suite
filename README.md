# Aurora Suite

An offline Aurora boxart/asset installer.

The GUI isn't winning any design awards, but it's simple to use. This is mainly an offline tool - you build your asset library locally - but it does need access to your console over XBDM or FTP to actually push things over.

## How it works

**Sync tab** - this is where you set your console's GameData path and point the tool at your local asset library. I've included a premade library you're welcome to use.

Point it at that library and hit **Load Library**. The app scans your game assets and lists everything in a grid, where you can pick exactly which Title IDs you want to transfer.

**Image Assets tab** - this is where you can build/customize your own asset library instead. Set your source folder (e.g. `Covers\`) and the tool auto-detects every Title ID inside it and converts them all. Works with either a single folder or a whole batch of them - your call.

The folder structure matters though, it has to follow this layout:

```
Folder\
  TitleID\
    Icon\xxx.png
    Coverart\xxx.png       (also accepts "Boxart")
    Background\xxx.png
    Screenshots\xxx.png
```

Screenshots is the only folder that can hold multiple images (up to 20) - everything else is one image per folder, no exceptions. That's just how Aurora reads these.

Once you've got what you want, tick the asset types you're converting and hit **Scan Source**. Then select whichever titles you actually want and hit **Convert Selected**. It'll convert everything and drop it in your output directory (or wherever else you pointed it).

The output folder will look like `TitleID\*.asset` files.

From there you can jump back to the Sync tab and push it all to your console.

<img width="1920" height="1080" alt="Transfering" src="https://github.com/user-attachments/assets/0271978a-c8d1-4e02-ac38-6f429409e474" />


<img width="1075" height="896" alt="Conversion" src="https://github.com/user-attachments/assets/c108829e-f7cd-4fdf-ad1f-079d4d7b00d3" />



Xbox 360 Game Covers ripped from XboxUnity.net
By /u/DaCukiMonsta, Complete as of 28th Feb 2021
Raw covers converted to .assets by Vonkova 7/14/2026

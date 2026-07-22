<img width="1942" height="809" alt="ChatGPT Image Jul 15, 2026, 10_49_33 PM" src="https://github.com/user-attachments/assets/b9de1417-359b-4d8f-b27c-e98136dac65d" />



# Aurora Suite

An offline Aurora boxart/asset installer.

The GUI isn't winning any design awards, but it's simple to use. This is mainly an offline tool - you build your asset library locally - but it does need access to your console over XBDM or FTP to actually push things over.

## How it works

**Sync tab** - this is where you set your console's GameData path and point the tool at your local asset library. I've included a premade library you're welcome to use.

Point it at that library and hit **Load Library**. The app scans your game assets and lists everything in a grid, where you can pick exactly which Title IDs you want to transfer.

**Image Assets tab** - this is where you can build/customize your own asset library instead. Set your source folder (e.g. `Covers\`) and the tool auto-detects every Title ID inside it and converts them all. Works with either a single folder or a whole batch of them - your call. 
Update: There has been some improvments such as hooking into the Content.db and sorting by grid#, Title ID, Fuzzy Matching via text and Media ID. This means it now can sort titles that use 00000000 TItle ID.
There is also a custom Cover art generator using templates for the cover headers and footers. See below for photos.

**Title Update*** - This is a W.I.P but it does work. What doesn't work is enabling them in Aurora. But they do get installed to your console. More testing needs to be done here but it works. Region filtering is pretty acurate but still has some hickups.

**Skins** - This tab will allow you to load your skins and see them with a preview. This will also allow you to change it in realtime. However this will require you to setup your paths as it uses an executable to get out of Aurora to patch the database then it will relaunch Aurora automatically. 


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

Video Demo:
https://youtube/JqpkoovPHkw

Photos:

<img width="1486" height="893" alt="1" src="https://github.com/user-attachments/assets/6607d100-e659-40f8-a8b8-4c3d0d42919d" />
<img width="1486" height="893" alt="2" src="https://github.com/user-attachments/assets/af8a3d19-eded-43dd-b11c-9ccc0fd7a736" />
<img width="1486" height="893" alt="3" src="https://github.com/user-attachments/assets/b9372afa-f1b4-4317-9678-e2971097b54a" />
<img width="1486" height="893" alt="4" src="https://github.com/user-attachments/assets/101f34df-767d-4a2e-970d-af0ae3e37f82" />
<img width="1472" height="880" alt="5" src="https://github.com/user-attachments/assets/bcb48e04-4a1a-49b4-b1c3-8dd74f3cacd7" />
<img width="686" height="856" alt="skins" src="https://github.com/user-attachments/assets/f5d94325-459d-4bf4-9bab-178f86847cba" />
<img width="1486" height="893" alt="image" src="https://github.com/user-attachments/assets/dae73fde-3796-4331-a9e6-cfd3e213dcca" />
<img width="1486" height="893" alt="image" src="https://github.com/user-attachments/assets/37ff3d76-bc75-4f10-9ca4-9e6ed5dd530f" />
<img width="1486" height="893" alt="image" src="https://github.com/user-attachments/assets/c83184ee-f190-4773-85eb-f2a5e40e5d1b" />






```
Xbox 360 Game Covers ripped from XboxUnity.net
By /u/DaCukiMonsta, Complete as of 28th Feb 2021
Raw covers converted to .assets by Vonkova 7/14/2026

Cover Templates by kira125
```


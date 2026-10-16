# Flasher

Puts Crowsnest firmware on CrowPanel boards over USB, for a new board and for every update
after. Nothing to install first: it downloads Espressif's esptool (5.3.1, checked against its
published SHA-256) into `%LOCALAPPDATA%\Crowsnest\esptool` the first time.

## Using it

1. Plug the boards in with USB-C data cables.
2. Double-click `Flash-Crowsnest.cmd`.
3. Pick a board by number, or `A` for all of them.

Each board shows its id on its waiting screen ("PANEL dccc6c"), and the same id is in
Crowsnest's settings window. If Crowsnest is running, the flasher closes it (it holds the
boards' ports) and starts it again at the end. Boards are flashed one at a time.

From a PowerShell prompt, `-Port` skips the questions:

```
.\Flash-Crowsnest.ps1 -Port COM4
.\Flash-Crowsnest.ps1 -Port all
.\Flash-Crowsnest.ps1 -Image D:\crowsnest-display-0.4.3.bin
```

A flash that is interrupted (cable pulled, PC asleep) doesn't harm the board: run the flasher
again. The ESP32-S3's download mode is in the chip's ROM, so a board can always be reached; at
worst, hold BOOT while plugging it in.

## The firmware file

The flasher takes a full image, `crowsnest-display-<version>.bin`: bootloader, partition table
and app in one file, written at address 0. It reads the version and project name from the
app's header inside the file, so it refuses anything that isn't Crowsnest firmware (including
the build's app-only `crowsnest-display.bin`).

It looks next to itself first, picking the highest version. Run from the repo with no image
there, it joins the last firmware build (`firmware/crowsnest-display/build`, as listed in its
`flash_args`) into `build/crowsnest-display-<version>.bin` and uses that. So during
development: `idf.py build`, then run the flasher.

To hand the flasher to someone else, zip this folder with that image beside the script.

The version is `PROJECT_VER` in `firmware/crowsnest-display/CMakeLists.txt`.

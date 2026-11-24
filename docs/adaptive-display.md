# Display and input

This describes the runtime the source tree builds (see [Building from source](reproduce.md)). Releases up to 1.2.0-pre.1 have three display modes instead, Compatibility, Auto fit and Fixed; their description is this file in the tag of each release, for example [v1.2.0-pre.1](https://github.com/H0yeR404/weims-vgpu/blob/v1.2.0-pre.1/docs/adaptive-display.md).

The VM is shown in a window of its own, which the runtime opens; Manager is where its settings are. Changes saved in Manager apply at the VM's next start.

## The window's size is macOS's resolution

**Display & Input → Startup resolution** is the size the window opens at: 800–4096 pixels wide and 600–2160 high. From then on macOS follows the window. Drag an edge, and when you let go macOS changes its resolution to the window's pixels and the image fills the window, one guest pixel to one host pixel. Nothing is scaled.

The guest's width is always a multiple of 16 pixels and its height even, which upstream requires so that a GPU can write the guest's surfaces in place. A window you drag to a size in between takes the next smaller such size as you let go, by at most 15 pixels in width and one in height. A maximized or borderless window keeps the size Windows gave it, and may show up to 15 columns of black at its right edge.

There is no display mode to choose. The `display_mode` key stays in a VM's configuration so that earlier versions can still read the file, and `Weims.Cli start --display-mode` is accepted and ignored.

**Start in borderless fullscreen** opens the window without a frame, filling its screen.

## Pointer and keyboard

With **Allow the pointer to leave the VM window freely** on, the pointer moves in and out of the window like in any other, a click is a click in macOS, and Windows keeps its own shortcuts such as Alt+Tab and the Windows key. The window's title ends in `free pointer (host shortcuts enabled)`. New VMs have it on.

With it off, a click in the image captures the keyboard and the pointer: the pointer stays over the image and Windows' shortcuts go to macOS. **Ctrl+Alt+Win** releases them. Games that read relative mouse motion need this mode: while macOS hides its cursor, the window hands the mouse's motion to the guest's own mouse.

The free pointer comes from this project's patch, so it is offered with the runtime built here and not with a build of upstream's own, which Manager can also run.

## Sound

The guest has a sound output device, played through Windows' default output. Where Windows has no output device the VM starts without sound. The host's microphone is not given to the guest.

## Logs

Each start has a folder under the VM's `logs`, opened from **Diagnostics & Logs**. `stderr.log` holds QEMU's and the graphics library's lines, beginning with the GPU it chose. A GPU that lacks something the library needs is named there.

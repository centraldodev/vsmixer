# VSMixer

VSMixer is a C# multitrack playback app for Windows and macOS.

## Stack

- UI: Avalonia, chosen for one C#/.NET desktop UI codebase across Windows and macOS.
- Audio engine: ManagedBass with BASS add-ons, prepared for multitrack playback, mixing, tempo, and pitch control.
- MIDI: Melanchall.DryWetMidi, prepared for play/pause and future controller mappings.

## Current Prototype

- Dark mixer layout close to the first visual reference.
- Top transport bar with play/pause, rewind, timers, BPM, meter, grid, pitch, metronome, guide voice, and pad toggles.
- Timeline with time ruler, summed waveform placeholder, measure ruler, and colored session regions.
- Add-session modal with name, start measure, and end measure fields.
- Vertical track strips with volume, pan, mute, solo, delete, and level meter placeholders.
- Master volume footer.
- Multitrack import through the `+ ADICIONAR` button.
- Play/pause starts and pauses all imported audio streams.

## Native Audio

ManagedBass needs the native BASS library at runtime. This project includes:

- `Native/win-x64/bass.dll`
- `Native/osx/libbass.dylib`

The project file copies the native library for the current build OS into the app output folder.

## Run

```powershell
dotnet run --project VSMixer.csproj
```

## Build

```powershell
dotnet build
```

# StoryLight

StoryLight is a Windows-first Avalonia desktop reader for:

- `.epub`
- `.txt`
- `.md`
- `.docx`
- `.doc` via Microsoft Word fallback conversion
- `.pdf`

## Features

- Library view with recent documents and saved progress
- Reader-friendly paged view for reflowable formats
- PDF page mode based on extracted text pages
- Adjustable zoom
- Offline read-aloud using local `sherpa-onnx` voice folders
- EPUB cover display in the library when embedded cover art is available
- Startup splash screen with custom icon and loading image
- Resume position and zoom persistence

## Requirements

- Windows with .NET 8 SDK installed
- Microsoft Word installed if you want legacy `.doc` support

## Build And Run

From `C:\Repos\StoryLight` in PowerShell:

```powershell
dotnet restore .\src\StoryLight.App\StoryLight.App.csproj
dotnet build .\src\StoryLight.App\StoryLight.App.csproj
dotnet run --project .\src\StoryLight.App\StoryLight.App.csproj
```

From WSL/Linux shell you can build the project, but run the app from Windows PowerShell:

```bash
dotnet restore src/StoryLight.App/StoryLight.App.csproj
dotnet build src/StoryLight.App/StoryLight.App.csproj
```

## Tests

From `C:\Repos\StoryLight` in PowerShell:

```powershell
dotnet test .\StoryLight.sln
```

The current test suite covers text normalization/parsing helpers and the queued audio provider used by read-aloud playback.

## Supported Formats

- `.epub`
- `.txt`
- `.md`
- `.docx`
- `.doc` through Word conversion
- `.pdf`

## Read-Aloud

StoryLight uses local `sherpa-onnx` voice folders. Voices are discovered from:

```text
%LocalAppData%\StoryLight\voices\sherpa-onnx\
```

Each voice must live in its own subfolder and include a `storylight.voice.json` manifest.

### Voice folder layout

Example:

```text
%LocalAppData%\StoryLight\voices\sherpa-onnx\kokoro-en\
  storylight.voice.json
  model.onnx
  voices.bin
  tokens.txt
  espeak-ng-data\
  dict\
  lexicon-us-en.txt
```

### Manifest example

Example Kokoro voice manifest:

```json
{
  "id": "kokoro-en",
  "displayName": "Kokoro English",
  "modelType": "kokoro",
  "speakerId": 0,
  "model": "model.onnx",
  "voices": "voices.bin",
  "tokens": "tokens.txt",
  "dataDir": "espeak-ng-data",
  "dictDir": "dict",
  "lexicon": "lexicon-us-en.txt",
  "lang": "en-us",
  "lengthScale": 1.0
}
```

Example VITS manifest:

```json
{
  "id": "vits-en",
  "displayName": "VITS English",
  "modelType": "vits",
  "speakerId": 0,
  "model": "model.onnx",
  "tokens": "tokens.txt",
  "dataDir": "espeak-ng-data",
  "dictDir": "dict",
  "lexicon": "lexicon.txt",
  "noiseScale": 0.667,
  "noiseScaleW": 0.8,
  "lengthScale": 1.0
}
```

Supported `modelType` values in StoryLight:

- `kokoro`
- `vits`

### Using voices in the app

1. Start StoryLight.
2. Click `Voices Folder`.
3. Add one or more sherpa voice folders with `storylight.voice.json`.
4. Return to StoryLight. The voice list refreshes when the window is activated.
5. Choose a voice, set the speech rate, and use `Read`, `Pause`, `Resume`, or `Stop`.

Read-aloud currently speaks the current page text.

## General Use

### Importing documents

1. Click `Import`.
2. Choose a supported file.
3. The document is added to the library and opened automatically.

### Opening an existing library item

1. Select a book from the library list.
2. Click `Open`.

### Removing items

- `Remove` removes the selected item from the StoryLight library only.
- `Delete EPUB` deletes the selected EPUB file from disk and removes it from the library.

### Navigation

- `Previous` and `Next` move between pages
- `A-` and `A+` change text size
- Keyboard:
  - Left Arrow: previous page
  - Right Arrow: next page
  - `Ctrl` + `+`: zoom in
  - `Ctrl` + `-`: zoom out

## Notes And Limitations

- Legacy `.doc` support requires Microsoft Word to be installed.
- PDF support currently reads extractable text content rather than rendering the original PDF layout.
- sherpa-onnx voices are not bundled with StoryLight. You need to supply local model files.
- StoryLight currently supports manifest-driven `kokoro` and `vits` sherpa voice folders.

## Troubleshooting

### No voices appear in the toolbar

Check that:

- you are running StoryLight on Windows
- your voice folder is under `%LocalAppData%\StoryLight\voices\sherpa-onnx\`
- each voice folder contains `storylight.voice.json`
- every file referenced by the manifest exists

### Read-aloud does not start

Check that:

- a document page is open
- a sherpa voice is selected
- the voice manifest points to valid model files
- Windows audio output is working normally

### Git tries to add `.vs`, `bin`, or `obj`

This repo now includes a `.gitignore`. If you already staged generated files, run:

```powershell
git rm -r --cached .vs src\StoryLight.App\bin src\StoryLight.App\obj
```

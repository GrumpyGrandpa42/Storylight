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
- Read-aloud controls using Windows speech voices
- Resume position and zoom persistence

## Running

```bash
dotnet restore src/StoryLight.App/StoryLight.App.csproj
dotnet build src/StoryLight.App/StoryLight.App.csproj
dotnet run --project src/StoryLight.App/StoryLight.App.csproj
```

## Notes

- Legacy `.doc` support requires Microsoft Word to be installed.
- PDF support currently reads extractable text content rather than rendering the original PDF layout.

# UnturnedAssets

Light-weight API for scanning Unturned asset files (v1 and v2).

Uses `Microsoft.Extensions.Logging.Abstractions`.

## Usage

### Scan a Folder Recursively
```cs
UnturnedAssetFinder assetFinder = new UnturnedAssetFinder(logger: null);

// a single-threaded synchronous alternative is available but much slower
List<UnturnedAssetFile> files = await assetFinder.ScanAsync(
                                  folder: @"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles",
                                  language: "English",
                                  token: CancellationToken.None);


foreach (UnturnedAssetFile file in files)
{
    Console.WriteLine(file.FriendlyNameOrAssetName);
}
```

### Read one file (plus localization if present)
```cs
UnturnedAssetFinder assetFinder = new UnturnedAssetFinder(logger: null);

// a synchronous alternative is available
UnturnedAssetFile? file = await assetFinder.TryReadAsync(
                            file: @"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items\Guns\Ace\Ace.dat",
                            language: "English");

if (file != null)
{
    Console.WriteLine(file.FriendlyNameOrAssetName);
}
else
{
    Console.WriteLine("File not found.");
}
```

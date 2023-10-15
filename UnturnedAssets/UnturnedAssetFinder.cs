using Microsoft.Extensions.Logging;
using SDG.Unturned;

namespace UnturnedAssets;

/// <summary>
/// Recursively searches for unturned asset files and interprets them as <see cref="UnturnedAssetFile"/>.
/// </summary>
/// <remarks>This class should be disposed of afterwards.</remarks>
public class UnturnedAssetFinder : IDisposable
{
    internal const int MaxFileSize = 8388608; // 8MB

    private readonly DatParser _parser = new DatParser();
    private readonly ILogger<UnturnedAssetFinder>? _logger;
    private static UnturnedNexus? _nexus;
    private static volatile int _semaphoreQueue;
    private volatile int _disposed;
    
    /// <param name="logger">Can be <see langword="null"/> to disable logging.</param>
    public UnturnedAssetFinder(ILogger<UnturnedAssetFinder>? logger)
    {
        Interlocked.Increment(ref _semaphoreQueue);

        // nexus is needed to get the type dictionary that unturned uses for v1 assets.
        UnturnedNexus? old = Interlocked.CompareExchange(ref _nexus, new UnturnedNexus(), null);
        if (old == null)
        {
            lock (_nexus)
            {
                try
                {
                    _nexus.initialize();
                }
                catch (ArgumentException)
                {
                    // ignored
                }
            }
        }

        _logger = logger;
    }
    public void Dispose()
    {
        int diposed = Interlocked.Exchange(ref _disposed, 1);
        if (diposed == 0)
            return;

        int val = Interlocked.Decrement(ref _semaphoreQueue);
        Interlocked.CompareExchange(ref _semaphoreQueue, 0, -1);
        if (val <= 0)
        {
            UnturnedNexus? nexus = Interlocked.Exchange(ref _nexus, null);
            if (nexus != null)
            {
                lock (nexus)
                    nexus.shutdown();
            }
        }
    }

    /// <summary>
    /// Returns a list of all asset files in the directory or its subdirectories.
    /// </summary>
    /// <param name="language">Helps pick a localization file if multiple are present. Always falls back to English.</param>
    /// <exception cref="ObjectDisposedException"/>
    /// <remarks>Has a max file size of 8MB.</remarks>
    public async Task<List<UnturnedAssetFile>> ScanAsync(string folder, string language = "English", CancellationToken token = default)
    {
        if (_disposed > 0 || _nexus == null)
            throw new ObjectDisposedException(nameof(UnturnedAssetFinder));
        List<UnturnedAssetFile> rtn = new List<UnturnedAssetFile>(128);
        List<Task> waiting = new List<Task>(512);
        DirectoryInfo dir = new DirectoryInfo(folder);
        foreach (FileInfo info in dir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            // looks for a lone asset file
            if (info.Extension.Equals(".asset", StringComparison.Ordinal))
            {
                string? dirName = Path.GetFileName(Path.GetDirectoryName(info.FullName));
                if (dirName != null && !dirName.Equals(Path.GetFileNameWithoutExtension(info.FullName), StringComparison.Ordinal))
                {
                    if (File.Exists(Path.Combine(dirName, dirName + ".asset")) ||
                        File.Exists(Path.Combine(dirName, dirName + ".dat")) ||
                        File.Exists(Path.Combine(dirName, "Asset.dat")))
                        continue;
                }
            }
            // looks for a dat file in its folder
            else if (info.Extension.Equals(".dat", StringComparison.Ordinal))
            {
                string? dirName = Path.GetFileName(Path.GetDirectoryName(info.FullName));
                if (dirName != null && !dirName.Equals(Path.GetFileNameWithoutExtension(info.FullName), StringComparison.Ordinal))
                    continue;
            }
            else continue;
            waiting.Add(Task.Run(async () =>
            {
                DatDictionary? dict = await ReadFileAsync(info.FullName, token).ConfigureAwait(false);
                if (dict == null)
                    return;
                
                UnturnedAssetFile? asset = TryRead(dict, info);
                if (asset != null)
                {
                    // ReSharper disable once InconsistentlySynchronizedField
                    await asset.CacheLocalAsync(_parser, token).ConfigureAwait(false);
                    lock (rtn)
                        rtn.Add(asset);
                }
            }, token));
        }

        await Task.WhenAll(waiting).ConfigureAwait(false);

        _logger?.LogDebug($"Assets discovered: {rtn.Count}.");
        lock (rtn)
        {
            return rtn;
        }
    }

    /// <summary>
    /// Returns a list of all asset files in the directory or its subdirectories.
    /// </summary>
    /// <param name="language">Helps pick a localization file if multiple are present. Always falls back to English.</param>
    /// <exception cref="ObjectDisposedException"/>
    /// <remarks>Has a max file size of 8MB.</remarks>
    public List<UnturnedAssetFile> Scan(string folder, string language = "English")
    {
        if (_disposed > 0 || _nexus == null)
            throw new ObjectDisposedException(nameof(UnturnedAssetFinder));
        List<UnturnedAssetFile> rtn = new List<UnturnedAssetFile>(32);
        DirectoryInfo dir = new DirectoryInfo(folder);
        foreach (FileInfo info in dir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            // looks for a lone asset file
            if (info.Extension.Equals(".asset", StringComparison.Ordinal))
            {
                string? dirName = Path.GetFileName(Path.GetDirectoryName(info.FullName));
                if (dirName != null && !dirName.Equals(Path.GetFileNameWithoutExtension(info.FullName), StringComparison.Ordinal))
                {
                    if (File.Exists(Path.Combine(dirName, dirName + ".asset")) ||
                        File.Exists(Path.Combine(dirName, dirName + ".dat")) ||
                        File.Exists(Path.Combine(dirName, "Asset.dat")))
                        continue;

                }
            }
            // looks for a dat file in its folder
            else if (info.Extension.Equals(".dat", StringComparison.Ordinal))
            {
                string? dirName = Path.GetFileName(Path.GetDirectoryName(info.FullName));
                if (dirName != null && !dirName.Equals(Path.GetFileNameWithoutExtension(info.FullName), StringComparison.Ordinal))
                    continue;
            }
            else continue;
            DatDictionary? dict = ReadFile(info.FullName);
            if (dict == null)
                continue;
            UnturnedAssetFile? asset = TryRead(dict, info, language);
            if (asset != null)
            {
                // ReSharper disable once InconsistentlySynchronizedField
                asset.CacheLocal(_parser);
                rtn.Add(asset);
            }
        }

        _logger?.LogDebug($"Assets discovered: {rtn.Count}.");
        return rtn;
    }

    /// <summary>
    /// Read one asset file's content and returns an <see cref="UnturnedAssetFile"/> representation, or <see langword="null"/> if the file is not a valid asset, doesn't exist, or is too big.
    /// </summary>
    /// <param name="language">Helps pick a localization file if multiple are present. Always falls back to English.</param>
    /// <exception cref="ObjectDisposedException"/>
    /// <remarks>Has a max file size of 8MB. Returns <see langword="null"/> if the file is too big.</remarks>
    public UnturnedAssetFile? TryRead(string file, string language = "English")
        => TryRead(new FileInfo(file), language);

    /// <summary>
    /// Read one asset file's content and returns an <see cref="UnturnedAssetFile"/> representation, or <see langword="null"/> if the file is not a valid asset, doesn't exist, or is too big.
    /// </summary>
    /// <param name="language">Helps pick a localization file if multiple are present. Always falls back to English.</param>
    /// <exception cref="ObjectDisposedException"/>
    /// <remarks>Has a max file size of 8MB. Returns <see langword="null"/> if the file is too big.</remarks>
    public UnturnedAssetFile? TryRead(FileInfo file, string language = "English")
    {
        if (_disposed > 0)
            throw new ObjectDisposedException(nameof(UnturnedAssetFinder));

        if (!file.Exists)
            return null;
        DatDictionary? data = ReadFile(file.FullName);
        if (data == null)
            return null;

        UnturnedAssetFile? asset = TryRead(data, file, language);
        asset?.CacheLocal(_parser);
        return asset;
    }

    /// <summary>
    /// Read one asset file's content and returns a <see cref="UnturnedAssetFile"/> representation, or <see langword="null"/> if the file is not a valid asset, doesn't exist, or is too big.
    /// </summary>
    /// <param name="language">Helps pick a localization file if multiple are present. Always falls back to English.</param>
    /// <exception cref="ObjectDisposedException"/>
    /// <remarks>Has a max file size of 8MB. Returns <see langword="null"/> if the file is too big.</remarks>
    public Task<UnturnedAssetFile?> TryReadAsync(string file, string language = "English")
        => TryReadAsync(new FileInfo(file), language);

    /// <summary>
    /// Read one asset file's content and returns an <see cref="UnturnedAssetFile"/> representation, or <see langword="null"/> if the file is not a valid asset, doesn't exist, or is too big.
    /// </summary>
    /// <param name="language">Helps pick a localization file if multiple are present. Always falls back to English.</param>
    /// <exception cref="ObjectDisposedException"/>
    /// <remarks>Has a max file size of 8MB. Returns <see langword="null"/> if the file is too big.</remarks>
    public async Task<UnturnedAssetFile?> TryReadAsync(FileInfo file, string language = "English")
    {
        if (_disposed > 0)
            throw new ObjectDisposedException(nameof(UnturnedAssetFinder));

        if (!file.Exists)
            return null;
        DatDictionary? data = await ReadFileAsync(file.FullName).ConfigureAwait(false);
        if (data == null)
            return null;

        UnturnedAssetFile? asset = TryRead(data, file, language);
        asset?.CacheLocal(_parser);
        return asset;
    }

    /// <summary>
    /// Read one asset file's <see cref="DatDictionary"/> and returns an <see cref="UnturnedAssetFile"/> representation, or <see langword="null"/> if the file is not a valid asset.
    /// </summary>
    /// <param name="language">Helps pick a localization file if multiple are present. Always falls back to English.</param>
    /// <exception cref="ObjectDisposedException"/>
    /// <remarks>Has a max file size of 8MB.</remarks>
    public UnturnedAssetFile? TryRead(DatDictionary dictionary, FileInfo file, string language = "English")
    {
        if (_disposed > 0 || _nexus == null)
            throw new ObjectDisposedException(nameof(UnturnedAssetFinder));
        lock (_nexus)
        {
            Guid guid;
            ushort id = 0;
            Type? assetType = null;
            // v2 metadata
            if (dictionary.TryGetDictionary("Metadata", out DatDictionary metadata))
            {
                if (!metadata.TryParseGuid("Guid", out guid))
                {
                    ReportError(file, "Missing Metadata.Guid property.");
                    return null;
                }
                assetType = metadata.ParseType("Type");
                if (assetType == null)
                {
                    ReportError(file, "Missing Metadata.Type property.");
                    return null;
                }
            }
            else
            {
                // v1 metadata
                dictionary.TryParseGuid("GUID", out guid);
                dictionary.TryParseUInt16("ID", out id);
            }
            if (assetType == null)
            {
                // v1 metadata
                if (!dictionary.TryGetString("Type", out string typeStr))
                {
                    ReportError(file, "Missing Type property.");
                    return null;
                }

                assetType = Assets.assetTypes.getType(typeStr);

                if (assetType == null)
                {
                    assetType = dictionary.ParseType("Type");

                    if (assetType == null)
                    {
                        ReportError(file, $"Unknown value: \"{typeStr}\" for Type property.");
                        return null;
                    }
                }
            }

            if (!typeof(Asset).IsAssignableFrom(assetType))
            {
                ReportError(file, $"Type {assetType.FullName} is not assignable from Asset.");
                return null;
            }

            UnturnedAssetFile asset = new UnturnedAssetFile(assetType, file, guid, id, dictionary, language);
            return asset;
        }
    }
    private void ReportError(FileInfo file, string message)
    {
        _logger?.LogWarning($"Asset \"{Path.GetFileNameWithoutExtension(file.FullName)}\" load error: \"{message}\".{Environment.NewLine}\tFile: {file.FullName}");
    }

    /// <summary>
    /// Reads a <see cref="DatDictionary"/> from an asset or localization file.
    /// </summary>
    /// <remarks>Has a max file size of 8MB. Returns <see langword="null"/> if the file is too big.</remarks>
    public DatDictionary? ReadFile(string path)
    {
        using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fileStream.Length > MaxFileSize)
        {
            return null;
        }

        using StreamReader inputReader = new StreamReader(fileStream);

        lock (_parser)
            return _parser.Parse(inputReader);
    }

    /// <summary>
    /// Reads a <see cref="DatDictionary"/> from an asset or localization file asynchronously.
    /// </summary>
    /// <remarks>Has a max file size of 8MB. Returns <see langword="null"/> if the file is too big.</remarks>
    public async Task<DatDictionary?> ReadFileAsync(string path, CancellationToken token = default)
    {
#if NETCOREAPP || NETSTANDARD
        await using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
#else
        using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
#endif
        if (fileStream.Length > MaxFileSize)
        {
            return null;
        }

        using MemoryStream stream = new MemoryStream((int)fileStream.Length);
        await fileStream.CopyToAsync(stream, 81920, token).ConfigureAwait(false);
        stream.Seek(0L, SeekOrigin.Begin);

        using StreamReader inputReader = new StreamReader(stream);

        lock (_parser)
            return _parser.Parse(inputReader);
    }
}

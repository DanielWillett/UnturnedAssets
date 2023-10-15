using SDG.Unturned;
using System.Globalization;

namespace UnturnedAssets;
public class UnturnedAssetFile
{
    private bool _cachedLocal;
    private bool _cachedFriendlyName;
    private DatDictionary? _local;
    private string? _friendlyName;
    public string AssetName { get; }
    public string? LocalPath { get; }
    public FileInfo File { get; }
    public DatDictionary Dictionary { get; }
    public Type AssetType { get; }
    public Guid Guid { get; }
    public ushort Id { get; }
    public EAssetType Category { get; }

    public string? FriendlyName
    {
        get
        {
            if (_cachedFriendlyName)
                return _friendlyName!;
            DatDictionary? lcl = Local;
            if (lcl != null)
                lcl.TryGetString("Name", out _friendlyName);
            _cachedFriendlyName = true;
            return _friendlyName;
        }
    }

    public string FriendlyNameOrAssetName => FriendlyName ?? AssetName;

    public DatDictionary? Local
    {
        get
        {
            if (!_cachedLocal)
                CacheLocal(null);
            return _local;
        }
    }
    public UnturnedAssetFile(Type assetType, FileInfo file, Guid guid, ushort id, DatDictionary dictionary, string language = "English")
    {
        AssetType = assetType;
        File = file;

        // if file name is 'Asset.dat', get the asset name from the parent folder.
        if (file.FullName.EndsWith("Asset.dat", StringComparison.Ordinal))
            AssetName = Path.GetFileName(Path.GetDirectoryName(file.FullName)!);
        else
            AssetName = Path.GetFileNameWithoutExtension(file.FullName);

        Dictionary = dictionary;
        Guid = guid;
        Id = id;
        string lclPath = Path.Combine(Path.GetDirectoryName(File.FullName)!, language + ".dat");

        // English.dat file
        if (System.IO.File.Exists(lclPath))
            LocalPath = lclPath;
        else if (language == null || !language.Equals("English", StringComparison.Ordinal))
            LocalPath = Path.Combine(Path.GetDirectoryName(File.FullName)!, "English.dat");

        // category from asset type
        if (typeof(ItemAsset).IsAssignableFrom(assetType))
            Category = EAssetType.ITEM;
        else if (typeof(EffectAsset).IsAssignableFrom(assetType))
            Category = EAssetType.EFFECT;
        else if (typeof(VehicleAsset).IsAssignableFrom(assetType))
            Category = EAssetType.VEHICLE;
        else if (typeof(ObjectAsset).IsAssignableFrom(assetType))
            Category = EAssetType.OBJECT;
        else if (typeof(ResourceAsset).IsAssignableFrom(assetType))
            Category = EAssetType.RESOURCE;
        else if (typeof(AnimalAsset).IsAssignableFrom(assetType))
            Category = EAssetType.ANIMAL;
        else if (typeof(MythicAsset).IsAssignableFrom(assetType))
            Category = EAssetType.MYTHIC;
        else if (typeof(SkinAsset).IsAssignableFrom(assetType))
            Category = EAssetType.SKIN;
        else if (typeof(SpawnAsset).IsAssignableFrom(assetType))
            Category = EAssetType.SPAWN;
        else if (typeof(DialogueAsset).IsAssignableFrom(assetType) || typeof(VendorAsset).IsAssignableFrom(assetType) || typeof(QuestAsset).IsAssignableFrom(assetType))
            Category = EAssetType.NPC;
        else Category = EAssetType.NONE;
    }
    internal void CacheLocal(DatParser? parser = null)
    {
        _cachedLocal = true;
        if (LocalPath == null || !System.IO.File.Exists(LocalPath))
            return;
        parser ??= new DatParser();
        using FileStream fileStream = new FileStream(LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using StreamReader inputReader = new StreamReader(fileStream);

        lock (parser)
            _local = parser.Parse(inputReader);
    }
    internal async Task CacheLocalAsync(DatParser? parser = null, CancellationToken token = default)
    {
        _cachedLocal = true;
        if (LocalPath == null || !System.IO.File.Exists(LocalPath))
            return;
        parser ??= new DatParser();
#if NETCOREAPP || NETSTANDARD
        await using FileStream fileStream = new FileStream(LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
#else
        using FileStream fileStream = new FileStream(LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
#endif

        if (fileStream.Length > UnturnedAssetFinder.MaxFileSize)
            return;

        using MemoryStream stream = new MemoryStream((int)fileStream.Length);
        await fileStream.CopyToAsync(stream, 81920, token).ConfigureAwait(false);
        stream.Seek(0L, SeekOrigin.Begin);

        using StreamReader inputReader = new StreamReader(stream);
        
        lock (parser)
            _local = parser.Parse(inputReader);
    }

    public override string ToString()
    {
        if (Id != 0)
            return Id.ToString(CultureInfo.InvariantCulture) + " | " + AssetName + " | " + AssetType.Name;
        
        return AssetName + " | " + AssetType.Name;
    }
}
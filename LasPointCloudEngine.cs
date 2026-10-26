using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using Autodesk.Revit.UI;
using WinForms = System.Windows.Forms;

namespace LASPointCloudEngine;

/// <summary>
/// Registers a native, file-based LAS point-cloud engine for the whole Revit session.
/// The LAS file remains an external link; this add-in never converts it into sampled model geometry.
/// </summary>
public sealed class App : IExternalApplication
{
    private const string EngineIdentifier = "las";
    private static LasEngine? _engine;
    private static bool _registered;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            bool alreadyRegistered = false;
            foreach (string identifier in PointCloudEngineRegistry.GetSupportedEngines())
            {
                if (string.Equals(identifier, EngineIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyRegistered = true;
                    break;
                }
            }

            // Do not replace an engine that was registered earlier by another add-in.
            if (!alreadyRegistered)
            {
                _engine = new LasEngine();
                PointCloudEngineRegistry.RegisterPointCloudEngine(EngineIdentifier, _engine, true);
                _registered = true;
            }
        }
        catch (Exception exception)
        {
            Autodesk.Revit.UI.TaskDialog.Show(
                "LAS Point Cloud",
                "Unable to register the LAS point-cloud engine.\n\n" + exception.Message);
            return Result.Failed;
        }

        // A ribbon failure must not disable the already-registered engine.
        try
        {
            const string tabName = "LAS Point Cloud";
            try { application.CreateRibbonTab(tabName); }
            catch { /* The tab may already exist. */ }

            RibbonPanel panel = application.CreateRibbonPanel(tabName, "LAS Point Cloud");
            PushButtonData button = new(
                "LASPointCloudEngine.Import",
                "Import LAS\nPoint Cloud",
                Assembly.GetExecutingAssembly().Location,
                typeof(ImportLasCommand).FullName!)
            {
                ToolTip = "Link an uncompressed LAS file as a native Revit point cloud. The complete source file remains external.",
                LongDescription = "Imports an uncompressed LAS 1.0–1.4 file without ReCap. " +
                                  "The engine keeps the LAS file as a Manage Links external point cloud and returns detail to Revit on demand.",
            };
            RibbonIconLoader.Apply(button, "ImportLas");
            panel.AddItem(button);

            panel.AddSeparator();

            PushButtonData createIndexedRcpButton = new(
                "LASPointCloudEngine.CreateIndexedRcp",
                "Create Indexed\nRCP from LAS",
                Assembly.GetExecutingAssembly().Location,
                typeof(CreateIndexedRcpFromLasCommand).FullName!)
            {
                ToolTip = "Use installed Autodesk ReCap Pro to create a high-detail indexed RCP/RCS point cloud from LAS.",
                LongDescription = "Runs Autodesk ReCap's supported background converter with decimation set to zero and no custom range-clipping settings. " +
                                  "Revit remains usable while the RCP is created. Choose a permanent local folder because the RCP and its support files are a linked dataset.",
            };
            RibbonIconLoader.Apply(createIndexedRcpButton, "CreateIndexedRcp");
            panel.AddItem(createIndexedRcpButton);

            PushButtonData linkIndexedRcpButton = new(
                "LASPointCloudEngine.LinkIndexedRcp",
                "Link Indexed\nRCP/RCS",
                Assembly.GetExecutingAssembly().Location,
                typeof(LinkIndexedRcpCommand).FullName!)
            {
                ToolTip = "Link a completed ReCap RCP or RCS using Revit's native indexed point-cloud engine.",
                LongDescription = "Links a completed RCP/RCS at the project origin without deleting or changing the existing LAS link. " +
                                  "Validate the new native point cloud before removing any earlier link.",
            };
            RibbonIconLoader.Apply(linkIndexedRcpButton, "LinkIndexedRcp");
            panel.AddItem(linkIndexedRcpButton);

            PushButtonData conversionStatusButton = new(
                "LASPointCloudEngine.ReCapConversionStatus",
                "RCP Indexing\nProgress",
                Assembly.GetExecutingAssembly().Location,
                typeof(ReCapConversionStatusCommand).FullName!)
            {
                ToolTip = "Open the live ReCap LAS-to-RCP indexing progress window for the conversion started during this Revit session.",
            };
            RibbonIconLoader.Apply(conversionStatusButton, "RcpIndexingProgress");
            panel.AddItem(conversionStatusButton);
        }
        catch
        {
            // The command remains available through External Tools when the ribbon cannot be created.
        }

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        if (_registered)
        {
            try { PointCloudEngineRegistry.UnregisterPointCloudEngine(EngineIdentifier); }
            catch { /* Revit may have already disposed the registry. */ }
        }

        _engine = null;
        _registered = false;
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class ImportLasCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document? document = commandData.Application.ActiveUIDocument?.Document;
        if (document is null || document.IsFamilyDocument)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Import LAS", "Open a Revit project before importing a LAS point cloud.");
            return Result.Cancelled;
        }

        using WinForms.OpenFileDialog picker = new()
        {
            Title = "Select uncompressed LAS point cloud",
            Filter = "LAS point cloud (*.las)|*.las",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog() != WinForms.DialogResult.OK)
            return Result.Cancelled;

        try
        {
            _ = LasHeader.Read(picker.FileName); // Validate before changing the model.
            using Transaction transaction = new(document, "Import LAS Point Cloud");
            transaction.Start();
            PointCloudType cloudType = PointCloudType.Create(document, "las", picker.FileName);
            PointCloudInstance.Create(document, cloudType.Id, Transform.Identity);
            transaction.Commit();
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            Autodesk.Revit.UI.TaskDialog.Show("Import LAS", "The LAS point cloud could not be linked.\n\n" + exception.Message);
            return Result.Failed;
        }
    }
}

public sealed class LasEngine : IPointCloudEngine
{
    public IPointCloudAccess CreatePointCloudAccess(string identifier) => new LasPointCloudAccess(identifier);
    public void Free() { }
}

/// <summary>
/// Access object for one LAS link. Coordinates are kept local to the LAS header offset to preserve
/// float precision; Revit receives the header offset separately for its point-cloud placement metadata.
/// </summary>
public sealed class LasPointCloudAccess : IPointCloudAccess
{
    // Rendering callbacks run inside Revit. These hard limits prevent a very large source file
    // from monopolising the UI thread while still leaving the entire LAS file linked externally.
    private const int DefaultRawLasBudget = 4_000;
    private const int MaximumRawLasBudget = 8_000;
    private const int MaximumRawLasDirectQueryPoints = 4_000;
    private const int DefaultCachedBudget = 160_000;
    private const int MaximumCachedBudget = 200_000;
    private const int MaximumCachedDirectQueryPoints = 25_000;

    private readonly string _path;
    private readonly LasHeader _header;
    private readonly LasPointCache? _cache;
    private readonly object _directReadLock = new();
    private LasPointQuery? _directQuery;
    private DirectQueryKey _directQueryKey;
    private bool _hasDirectQuery;
    private bool _freed;

    public LasPointCloudAccess(string path)
    {
        _path = path;
        _header = LasHeader.Read(path);
        _cache = LasPointCache.TryLoad(path, _header);
    }

    public double GetUnitsToFeetConversionFactor() => LasHeader.UnitsToFeet;
    public XYZ GetOffset() => new(_header.OffsetX, _header.OffsetY, _header.OffsetZ);
    public Outline GetExtent() => new(_header.LocalMinimum, _header.LocalMaximum);
    public string GetName() => Path.GetFileName(_path);
    public PointCloudColorEncoding GetColorEncoding() => PointCloudColorEncoding.ABGR;

    // Revit controls how much detail it requests for a view. The temporary display sample is
    // density-aware; the original LAS remains the full-fidelity external source.
    public IPointSetIterator CreatePointSetIterator(PointCloudFilter filter, double density, ElementId viewId) =>
        CreateIterator(filter, GetRenderBudget(density));

    public IPointSetIterator CreatePointSetIterator(PointCloudFilter filter, ElementId viewId) =>
        CreateIterator(filter, GetDefaultDisplayBudget());

    public unsafe int ReadPoints(PointCloudFilter filter, ElementId viewId, IntPtr buffer, int maximumPoints)
    {
        if (buffer == IntPtr.Zero || maximumPoints <= 0)
            return 0;

        try
        {
            lock (_directReadLock)
            {
                if (_freed)
                    return 0;

                DirectQueryKey key = new(filter, viewId?.Value ?? ElementId.InvalidElementId.Value);
                if (!_hasDirectQuery || !_directQueryKey.Equals(key))
                {
                    _directQuery?.Dispose();
                    int directBudget = _cache is null
                        ? MaximumRawLasDirectQueryPoints
                        : MaximumCachedDirectQueryPoints;
                    _directQuery = new LasPointQuery(
                        _path,
                        _header,
                        _cache,
                        filter,
                        directBudget,
                        directBudget);
                    _directQueryKey = key;
                    _hasDirectQuery = true;
                }

                int written = _directQuery.ReadPoints(buffer, maximumPoints);
                return written;
            }
        }
        catch
        {
            // Rendering callbacks must not let damaged/moved source data escape into Revit.
            return 0;
        }
    }

    public void Free()
    {
        lock (_directReadLock)
        {
            _freed = true;
            _directQuery?.Dispose();
            _directQuery = null;
            _hasDirectQuery = false;
        }
    }

    private IPointSetIterator CreateIterator(PointCloudFilter? filter, int renderBudget)
    {
        try
        {
            if (_freed)
                return EmptyPointSetIterator.Instance;
            return new LasPointSetIterator(new LasPointQuery(
                _path,
                _header,
                _cache,
                filter,
                renderBudget,
                renderBudget));
        }
        catch
        {
            return EmptyPointSetIterator.Instance;
        }
    }

    private int GetRenderBudget(double density)
    {
        int defaultBudget = GetDefaultDisplayBudget();
        int maximumBudget = _cache is null ? MaximumRawLasBudget : MaximumCachedBudget;
        if (!double.IsFinite(density) || density <= 0)
            return defaultBudget;

        // The API defines density in native point-cloud units. Use the cloud footprint, not a
        // per-view count that could make a broad view invoke an unbounded file scan.
        double width = Math.Max(1.0, _header.LocalMaximum.X - _header.LocalMinimum.X);
        double depth = Math.Max(1.0, _header.LocalMaximum.Y - _header.LocalMinimum.Y);
        double requested = density * width * depth;
        if (!double.IsFinite(requested))
            return maximumBudget;

        return (int)Math.Clamp(
            Math.Max((double)defaultBudget, Math.Ceiling(requested)),
            1.0,
            (double)maximumBudget);
    }

    private int GetDefaultDisplayBudget() => _cache is null
        ? DefaultRawLasBudget
        : Math.Min(DefaultCachedBudget, _cache.Count);

    private readonly struct DirectQueryKey : IEquatable<DirectQueryKey>
    {
        private readonly PointCloudFilter? _filter;
        private readonly long _viewId;

        public DirectQueryKey(PointCloudFilter? filter, long viewId)
        {
            _filter = filter;
            _viewId = viewId;
        }

        public bool Equals(DirectQueryKey other) =>
            ReferenceEquals(_filter, other._filter) && _viewId == other._viewId;

        public override bool Equals(object? obj) => obj is DirectQueryKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            _filter is null ? 0 : RuntimeHelpers.GetHashCode(_filter), _viewId);
    }
}

public sealed class LasPointSetIterator : IPointSetIterator
{
    private LasPointQuery? _query;

    public LasPointSetIterator(LasPointQuery query) => _query = query;

    public unsafe int ReadPoints(IntPtr buffer, int maximumPoints)
    {
        try { return _query?.ReadPoints(buffer, maximumPoints) ?? 0; }
        catch { return 0; }
    }

    public void Free()
    {
        _query?.Dispose();
        _query = null;
    }
}

public sealed class EmptyPointSetIterator : IPointSetIterator
{
    public static EmptyPointSetIterator Instance { get; } = new();
    private EmptyPointSetIterator() { }
    public int ReadPoints(IntPtr buffer, int maximumPoints) => 0;
    public void Free() { }
}

/// <summary>
/// A bounded display/query stream. It never scans an entire large LAS file from a Revit drawing
/// callback. The original LAS remains the full external link; a future spatial index can replace
/// this temporary, density-aware preview stream for full-resolution queries.
/// </summary>
public sealed class LasPointQuery : IDisposable
{
    private readonly LasHeader _header;
    private readonly LasPointCache? _cache;
    private readonly PointCloudFilter? _filter;
    private readonly FileStream? _stream;
    private readonly byte[]? _recordBuffer;
    private readonly object _readLock = new();
    private readonly long _sourcePointCount;
    private readonly long _maximumCandidates;
    private readonly int _maximumReturned;
    private readonly long _stride;
    private readonly FilterMode _filterMode;
    private long _nextRecord;
    private long _examined;
    private int _returned;

    public bool IsComplete { get; private set; }

    public LasPointQuery(
        string path,
        LasHeader header,
        LasPointCache? cache,
        PointCloudFilter? filter,
        int maximumReturned,
        int maximumCandidates)
    {
        _header = header;
        _cache = cache;
        _filter = filter;
        _sourcePointCount = cache?.Count ?? header.PointCount;
        _maximumReturned = (int)Math.Clamp(
            (long)maximumReturned,
            1L,
            Math.Min((long)int.MaxValue, _sourcePointCount));
        _maximumCandidates = Math.Clamp(
            (long)maximumCandidates,
            (long)_maximumReturned,
            _sourcePointCount);
        _stride = cache is null
            ? Math.Max(1L, DivideRoundUp(_sourcePointCount, _maximumCandidates))
            : 1L;
        if (cache is null)
        {
            _stream = LasHeader.OpenStream(path);
            _recordBuffer = new byte[header.RecordLength];
        }

        if (filter is null)
        {
            _filterMode = FilterMode.AcceptAll;
            return;
        }

        int cellResult = filter.TestCell(header.LocalMinimum, header.LocalMaximum);
        if (cellResult < 0)
        {
            _filterMode = FilterMode.RejectAll;
            IsComplete = true;
        }
        else if (cellResult > 0)
        {
            _filterMode = FilterMode.AcceptAll;
        }
        else
        {
            _filterMode = FilterMode.TestEveryPoint;
            filter.PrepareForCell(
                header.LocalMinimum,
                header.LocalMaximum,
                (int)Math.Min(_maximumCandidates, int.MaxValue));
        }
    }

    public unsafe int ReadPoints(IntPtr buffer, int maximumPoints)
    {
        if (buffer == IntPtr.Zero || maximumPoints <= 0)
            return 0;

        lock (_readLock)
        {
            if (IsComplete)
                return 0;

            CloudPoint* destination = (CloudPoint*)buffer.ToPointer();
            int written = 0;
            int writable = Math.Min(maximumPoints, _maximumReturned - _returned);
            while (_examined < _maximumCandidates &&
                   _nextRecord < _sourcePointCount &&
                   written < writable)
            {
                long recordIndex = _nextRecord;
                _nextRecord = AdvanceSequential(_nextRecord, _stride, _sourcePointCount);
                _examined++;

                CloudPoint point = _cache is null
                    ? _header.ReadPoint(_stream!, recordIndex, _recordBuffer!)
                    : _cache.GetPoint((int)recordIndex);
                if (_filterMode != FilterMode.TestEveryPoint || _filter!.TestPoint(point))
                {
                    destination[written++] = point;
                    _returned++;
                }
            }

            if (_examined >= _maximumCandidates ||
                _nextRecord >= _sourcePointCount ||
                _returned >= _maximumReturned)
            {
                IsComplete = true;
            }
            return written;
        }
    }

    public void Dispose()
    {
        lock (_readLock)
        {
            IsComplete = true;
            _stream?.Dispose();
        }
    }

    private static long AdvanceSequential(long current, long stride, long count)
    {
        if (current >= count - stride)
            return count;
        return current + stride;
    }

    private static long DivideRoundUp(long dividend, long divisor) =>
        dividend / divisor + (dividend % divisor == 0 ? 0 : 1);

    private enum FilterMode
    {
        RejectAll,
        AcceptAll,
        TestEveryPoint,
    }
}

/// <summary>
/// Header and point-record reader for uncompressed LAS 1.0–1.4. LAS has no mandatory linear-unit
/// field, so this initial release assumes the usual survey metric coordinates (metres).
/// </summary>
public sealed class LasHeader
{
    private static readonly int[] MinimumRecordLengths = { 20, 28, 26, 34, 57, 63, 30, 36, 38, 59, 67 };
    private static readonly int[] MinimumHeaderLengths = { 227, 227, 227, 235, 375 };
    public const double UnitsToFeet = 3.280839895; // metres -> feet

    public long PointDataOffset { get; private init; }
    public int RecordLength { get; private init; }
    public int ColorByteOffset { get; private init; }
    public long PointCount { get; private init; }
    public double ScaleX { get; private init; }
    public double ScaleY { get; private init; }
    public double ScaleZ { get; private init; }
    public double OffsetX { get; private init; }
    public double OffsetY { get; private init; }
    public double OffsetZ { get; private init; }
    public double MinX { get; private init; }
    public double MinY { get; private init; }
    public double MinZ { get; private init; }
    public double MaxX { get; private init; }
    public double MaxY { get; private init; }
    public double MaxZ { get; private init; }

    public XYZ LocalMinimum => new(MinX - OffsetX, MinY - OffsetY, MinZ - OffsetZ);
    public XYZ LocalMaximum => new(MaxX - OffsetX, MaxY - OffsetY, MaxZ - OffsetZ);

    public static LasHeader Read(string path)
    {
        // A cloud-only OneDrive placeholder turns every point read into an on-demand network
        // recall. Fail before the transaction rather than making Revit look unresponsive.
        if ((File.GetAttributes(path) & FileAttributes.Offline) != 0)
        {
            throw new InvalidDataException(
                "The LAS file is cloud-only. In OneDrive choose 'Always keep on this device' " +
                "or make a local copy before importing it.");
        }

        FileInfo info = new(path);
        if (info.Length < 227)
            throw new InvalidDataException("The selected file is too small to contain a LAS header.");

        using BinaryReader reader = OpenReader(path);
        if (Encoding.ASCII.GetString(ReadExactly(reader, 4)) != "LASF")
            throw new InvalidDataException("The selected file is not a LAS file.");

        reader.BaseStream.Seek(24, SeekOrigin.Begin);
        byte major = reader.ReadByte();
        byte minor = reader.ReadByte();
        if (major != 1 || minor > 4)
            throw new InvalidDataException("Only uncompressed LAS versions 1.0 through 1.4 are supported.");

        reader.BaseStream.Seek(94, SeekOrigin.Begin);
        ushort headerSize = reader.ReadUInt16();
        uint pointDataOffset = reader.ReadUInt32();
        if (headerSize < MinimumHeaderLengths[minor])
            throw new InvalidDataException("The LAS header is incomplete or damaged.");

        reader.BaseStream.Seek(104, SeekOrigin.Begin);
        byte rawFormat = reader.ReadByte();
        if ((rawFormat & 0x80) != 0)
            throw new InvalidDataException("Compressed LAZ data is not supported. Choose an uncompressed .las file.");
        int pointFormat = rawFormat & 0x3f;
        ushort recordLength = reader.ReadUInt16();
        uint legacyCount = reader.ReadUInt32();
        if (pointFormat > 10 || recordLength < MinimumRecordLengths[pointFormat])
            throw new InvalidDataException("The LAS point-record format is unsupported or damaged.");
        if (pointDataOffset < headerSize || pointDataOffset >= info.Length)
            throw new InvalidDataException("The LAS point-data offset is invalid.");

        reader.BaseStream.Seek(131, SeekOrigin.Begin);
        double sx = reader.ReadDouble(), sy = reader.ReadDouble(), sz = reader.ReadDouble();
        double ox = reader.ReadDouble(), oy = reader.ReadDouble(), oz = reader.ReadDouble();
        double maxX = reader.ReadDouble(), minX = reader.ReadDouble();
        double maxY = reader.ReadDouble(), minY = reader.ReadDouble();
        double maxZ = reader.ReadDouble(), minZ = reader.ReadDouble();

        if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(sz) || sx <= 0 || sy <= 0 || sz <= 0 ||
            !double.IsFinite(ox) || !double.IsFinite(oy) || !double.IsFinite(oz) ||
            !double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(minZ) ||
            !double.IsFinite(maxX) || !double.IsFinite(maxY) || !double.IsFinite(maxZ) ||
            minX > maxX || minY > maxY || minZ > maxZ)
            throw new InvalidDataException("The LAS coordinate scale, offset, or bounds are invalid.");

        long count = legacyCount;
        if (minor == 4)
        {
            if (headerSize < 255)
                throw new InvalidDataException("The LAS 1.4 extended point-count field is missing.");
            reader.BaseStream.Seek(247, SeekOrigin.Begin);
            ulong extendedCount = reader.ReadUInt64();
            if (extendedCount > long.MaxValue)
                throw new InvalidDataException("The LAS file contains more points than this engine can address.");
            if (extendedCount > 0)
                count = (long)extendedCount;
        }

        long bytesRequired;
        try { bytesRequired = checked(count * (long)recordLength); }
        catch (OverflowException) { throw new InvalidDataException("The LAS point-data size is invalid."); }
        if (count < 1 || bytesRequired > info.Length - (long)pointDataOffset)
            throw new InvalidDataException("The LAS file has no complete readable point records.");

        return new LasHeader
        {
            PointDataOffset = pointDataOffset,
            RecordLength = recordLength,
            ColorByteOffset = GetColorByteOffset(pointFormat),
            PointCount = count,
            ScaleX = sx,
            ScaleY = sy,
            ScaleZ = sz,
            OffsetX = ox,
            OffsetY = oy,
            OffsetZ = oz,
            MinX = minX,
            MinY = minY,
            MinZ = minZ,
            MaxX = maxX,
            MaxY = maxY,
            MaxZ = maxZ,
        };
    }

    public static BinaryReader OpenReader(string path) => new(new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite,
        bufferSize: 64 * 1024,
        options: FileOptions.RandomAccess));

    public static FileStream OpenStream(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite,
        bufferSize: 4 * 1024,
        options: FileOptions.RandomAccess);

    public CloudPoint ReadPoint(FileStream stream, long index, byte[] recordBuffer)
    {
        if (recordBuffer.Length < RecordLength)
            throw new ArgumentException("The point record buffer is too small.", nameof(recordBuffer));

        long recordOffset = checked(PointDataOffset + checked(index * (long)RecordLength));
        stream.Seek(recordOffset, SeekOrigin.Begin);
        int read = 0;
        while (read < RecordLength)
        {
            int bytesRead = stream.Read(recordBuffer, read, RecordLength - read);
            if (bytesRead == 0)
                throw new EndOfStreamException("Unexpected end of LAS point data.");
            read += bytesRead;
        }

        int x = BinaryPrimitives.ReadInt32LittleEndian(recordBuffer.AsSpan(0, 4));
        int y = BinaryPrimitives.ReadInt32LittleEndian(recordBuffer.AsSpan(4, 4));
        int z = BinaryPrimitives.ReadInt32LittleEndian(recordBuffer.AsSpan(8, 4));

        int color = 0x00ffffff; // opaque-white in Windows ABGR/COLORREF layout.
        if (ColorByteOffset >= 0)
        {
            ushort red = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(ColorByteOffset, 2));
            ushort green = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(ColorByteOffset + 2, 2));
            ushort blue = BinaryPrimitives.ReadUInt16LittleEndian(recordBuffer.AsSpan(ColorByteOffset + 4, 2));
            color = (red >> 8) | ((green >> 8) << 8) | ((blue >> 8) << 16);
        }

        return new CloudPoint(
            (float)(x * ScaleX),
            (float)(y * ScaleY),
            (float)(z * ScaleZ),
            color);
    }

    private static int GetColorByteOffset(int pointFormat) => pointFormat switch
    {
        2 => 20,
        3 or 5 => 28,
        7 or 8 or 10 => 30,
        _ => -1,
    };

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException("Unexpected end of LAS header.");
        return bytes;
    }
}

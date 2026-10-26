using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB.PointClouds;

namespace LASPointCloudEngine;

/// <summary>
/// A locally stored, spatially balanced preview cache. It is an acceleration artifact only: the
/// LAS file remains the authoritative source and is never changed by this engine.
/// </summary>
public sealed class LasPointCache
{
    private const string Magic = "LPCACHE2";
    private const int Version = 2;
    private const int MaximumPointCount = 2_000_000;

    private readonly CloudPoint[] _points;

    private LasPointCache(CloudPoint[] points) => _points = points;

    public int Count => _points.Length;

    public CloudPoint GetPoint(int index) => _points[index];

    public static LasPointCache? TryLoad(string sourcePath, LasHeader header)
    {
        try
        {
            FileInfo source = new(sourcePath);
            string cachePath = GetCachePath(sourcePath);
            if (!File.Exists(cachePath))
                return null;

            using FileStream stream = new(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 256 * 1024,
                options: FileOptions.SequentialScan);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || Encoding.ASCII.GetString(magic) != Magic)
                return null;
            if (reader.ReadInt32() != Version)
                return null;

            long sourceLength = reader.ReadInt64();
            long sourceWriteTicks = reader.ReadInt64();
            long sourcePointCount = reader.ReadInt64();
            int pointCount = reader.ReadInt32();
            if (sourceLength != source.Length ||
                sourceWriteTicks != source.LastWriteTimeUtc.Ticks ||
                sourcePointCount != header.PointCount ||
                pointCount < 1 || pointCount > MaximumPointCount)
            {
                return null;
            }

            long bytesRequired = checked((long)pointCount * 16L);
            if (stream.Length - stream.Position < bytesRequired)
                return null;

            CloudPoint[] points = new CloudPoint[pointCount];
            for (int index = 0; index < pointCount; index++)
            {
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                float z = reader.ReadSingle();
                int color = reader.ReadInt32();
                points[index] = new CloudPoint(x, y, z, color);
            }

            return new LasPointCache(points);
        }
        catch
        {
            // A missing, stale, or interrupted cache simply falls back to the safe raw-LAS path.
            return null;
        }
    }

    public static string GetCachePath(string sourcePath)
    {
        string normalized = Path.GetFullPath(sourcePath).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string filename = Convert.ToHexString(hash) + ".lpcache";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LASPointCloudEngine",
            "Cache",
            filename);
    }
}

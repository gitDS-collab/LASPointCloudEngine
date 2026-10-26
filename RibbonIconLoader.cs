using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace LASPointCloudEngine;

/// <summary>
/// Loads optional ribbon images from beside the installed add-in DLL.
/// A missing or unreadable asset deliberately leaves the button usable without an icon.
/// </summary>
internal static class RibbonIconLoader
{
    internal static void Apply(PushButtonData button, string iconName)
    {
        ImageSource? smallImage = Load(iconName, 16);
        if (smallImage is not null)
        {
            button.Image = smallImage;
        }

        ImageSource? largeImage = Load(iconName, 32);
        if (largeImage is not null)
        {
            button.LargeImage = largeImage;
        }
    }

    private static ImageSource? Load(string iconName, int size)
    {
        try
        {
            string? addinDirectory = Path.GetDirectoryName(typeof(App).Assembly.Location);
            if (string.IsNullOrWhiteSpace(addinDirectory))
            {
                return null;
            }

            string path = Path.Combine(addinDirectory, "Resources", "Icons", $"{iconName}{size}.png");
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            PngBitmapDecoder decoder = new(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            // Ribbon imagery is optional and must never prevent the LAS engine from starting.
            return null;
        }
    }
}

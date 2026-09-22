using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using SwingStudio.Session;

namespace SwingStudio.Capture;

public static class CameraModeLister
{
    private static readonly Dictionary<string, IReadOnlyList<CaptureMode>> Cached = new(StringComparer.OrdinalIgnoreCase);

    public static void Remember(string devicePath, IReadOnlyList<CaptureMode> modes)
    {
        Cached[devicePath] = modes;
    }

    public static IReadOnlyList<CaptureMode> Recall(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
        {
            return [];
        }

        return Cached.TryGetValue(devicePath, out var modes) ? modes : [];
    }

    public static IReadOnlyList<CaptureMode> List(string devicePath)
    {
        var modes = new List<CaptureMode>();
        var devEnum = (ICreateDevEnum)new CreateDevEnum();
        var category = VideoInputDeviceCategory;
        var hr = devEnum.CreateClassEnumerator(ref category, out var enumerator, 0);
        if (hr != 0 || enumerator is null)
        {
            Marshal.ReleaseComObject(devEnum);
            return modes;
        }

        CreateBindCtx(0, out var bindContext);
        try
        {
            var fetched = new IMoniker[1];
            while (enumerator.Next(1, fetched, IntPtr.Zero) == 0)
            {
                var moniker = fetched[0];
                try
                {
                    moniker.GetDisplayName(bindContext, null!, out var path);
                    if (!string.Equals(path, devicePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ReadModes(moniker, bindContext, modes);
                    break;
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(bindContext);
            Marshal.ReleaseComObject(enumerator);
            Marshal.ReleaseComObject(devEnum);
        }

        return Deduplicate(modes);
    }

    private static void ReadModes(IMoniker moniker, IBindCtx bindContext, List<CaptureMode> modes)
    {
        var filterId = typeof(IBaseFilter).GUID;
        moniker.BindToObject(bindContext, null!, ref filterId, out var bound);
        if (bound is not IBaseFilter filter)
        {
            return;
        }

        try
        {
            if (filter.EnumPins(out var pins) != 0 || pins is null)
            {
                return;
            }

            try
            {
                IPin? fallback = null;
                var fetched = new IPin[1];
                while (pins.Next(1, fetched, IntPtr.Zero) == 0)
                {
                    var pin = fetched[0];
                    if (pin.QueryDirection(out var direction) != 0 || direction != PinDirectionOutput)
                    {
                        Marshal.ReleaseComObject(pin);
                        continue;
                    }

                    if (IsCapturePin(pin))
                    {
                        var countBefore = modes.Count;
                        AppendPinModes(pin, modes);
                        Marshal.ReleaseComObject(pin);
                        if (modes.Count > countBefore)
                        {
                            ReleasePin(ref fallback);
                            return;
                        }

                        continue;
                    }

                    ReleasePin(ref fallback);
                    fallback = pin;
                }

                if (fallback is not null)
                {
                    AppendPinModes(fallback, modes);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(pins);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(filter);
        }
    }

    private static void AppendPinModes(IPin pin, List<CaptureMode> modes)
    {
        if (pin is not IAMStreamConfig config)
        {
            return;
        }

        if (config.GetNumberOfCapabilities(out var count, out var size) != 0 || count <= 0 || size <= 0)
        {
            return;
        }

        var caps = Marshal.AllocCoTaskMem(size);
        try
        {
            for (var index = 0; index < count; index++)
            {
                if (config.GetStreamCaps(index, out var mediaType, caps) != 0 || mediaType == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (TryReadMode(mediaType, caps, out var mode))
                    {
                        modes.Add(mode);
                    }
                }
                finally
                {
                    DeleteMediaType(mediaType);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(caps);
        }
    }

    private static bool TryReadMode(IntPtr mediaTypePointer, IntPtr caps, out CaptureMode mode)
    {
        mode = new CaptureMode();
        var mediaType = Marshal.PtrToStructure<AmMediaType>(mediaTypePointer);
        if (mediaType.MajorType != MediaTypeVideo || mediaType.FormatPtr == IntPtr.Zero)
        {
            return false;
        }

        if (!TryFormat(mediaType.SubType, out var fourCc))
        {
            return false;
        }

        int width;
        int height;
        long frameTime;
        if (mediaType.FormatType == FormatVideoInfo)
        {
            var header = Marshal.PtrToStructure<VideoInfoHeader>(mediaType.FormatPtr);
            width = header.Bitmap.Width;
            height = Math.Abs(header.Bitmap.Height);
            frameTime = header.AvgTimePerFrame;
        }
        else if (mediaType.FormatType == FormatVideoInfo2)
        {
            var header = Marshal.PtrToStructure<VideoInfoHeader2>(mediaType.FormatPtr);
            width = header.Bitmap.Width;
            height = Math.Abs(header.Bitmap.Height);
            frameTime = header.AvgTimePerFrame;
        }
        else
        {
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            var range = Marshal.PtrToStructure<VideoStreamConfigCaps>(caps);
            if (range.MinOutputSize.Width > 0 && range.MinOutputSize.Width == range.MaxOutputSize.Width)
            {
                width = range.MinOutputSize.Width;
                height = range.MinOutputSize.Height;
            }
        }

        if (frameTime <= 0)
        {
            var range = Marshal.PtrToStructure<VideoStreamConfigCaps>(caps);
            frameTime = range.MinFrameInterval;
        }

        if (width <= 0 || height <= 0 || frameTime <= 0)
        {
            return false;
        }

        var framesPerSecond = Math.Round(10_000_000d / frameTime, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(framesPerSecond - Math.Round(framesPerSecond)) < 0.05)
        {
            framesPerSecond = Math.Round(framesPerSecond);
        }

        if (framesPerSecond is < 1 or > 1000)
        {
            return false;
        }

        mode.Width = width;
        mode.Height = height;
        mode.FramesPerSecond = framesPerSecond;
        mode.FourCc = fourCc;
        return true;
    }

    private static bool TryFormat(Guid subtype, out string fourCc)
    {
        var bytes = subtype.ToByteArray();
        ReadOnlySpan<byte> fourCcSuffix = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];
        if (bytes.AsSpan(4).SequenceEqual(fourCcSuffix))
        {
            fourCc = Encoding.ASCII.GetString(bytes, 0, 4).TrimEnd('\0', ' ');
            return fourCc.Length > 0;
        }

        fourCc = "";
        return false;
    }

    private static List<CaptureMode> Deduplicate(List<CaptureMode> modes)
    {
        var unique = new List<CaptureMode>();
        foreach (var mode in modes.OrderByDescending(mode => mode.Width * mode.Height).ThenByDescending(mode => mode.FramesPerSecond).ThenBy(mode => mode.FourCc))
        {
            if (unique.Any(existing => existing.Matches(mode.Width, mode.Height, mode.FramesPerSecond, mode.FourCc)))
            {
                continue;
            }

            unique.Add(mode);
        }

        return unique;
    }

    private static bool IsCapturePin(IPin pin)
    {
        if (pin is not IKsPropertySet properties)
        {
            return false;
        }

        var set = PinPropertySet;
        var buffer = Marshal.AllocCoTaskMem(16);
        try
        {
            if (properties.Get(ref set, PinCategoryProperty, IntPtr.Zero, 0, buffer, 16, out _) != 0)
            {
                return false;
            }

            return Marshal.PtrToStructure<Guid>(buffer) == PinCategoryCapture;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static void ReleasePin(ref IPin? pin)
    {
        if (pin is null)
        {
            return;
        }

        Marshal.ReleaseComObject(pin);
        pin = null;
    }

    private static void DeleteMediaType(IntPtr mediaTypePointer)
    {
        var mediaType = Marshal.PtrToStructure<AmMediaType>(mediaTypePointer);
        if (mediaType.FormatPtr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(mediaType.FormatPtr);
        }

        if (mediaType.UnkPtr != IntPtr.Zero)
        {
            Marshal.Release(mediaType.UnkPtr);
        }

        Marshal.FreeCoTaskMem(mediaTypePointer);
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);

    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatVideoInfo = new("05589F80-C356-11CE-BF01-00AA0055595A");
    private static readonly Guid FormatVideoInfo2 = new("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA");
    private static readonly Guid PinCategoryCapture = new("FB6C4281-0353-11D1-905F-0000C0CC16BA");
    private static readonly Guid PinPropertySet = new("9B00F101-1567-11D1-B3F1-00AA003761C5");
    private const int PinCategoryProperty = 0;
    private const int PinDirectionOutput = 1;

    [ComImport]
    [Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86")]
    private class CreateDevEnum
    {
    }

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(ref Guid type, out IEnumMoniker? enumerator, int flags);
    }

    [ComImport]
    [Guid("56a86895-0ad4-11ce-b03a-0020af0ba770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBaseFilter
    {
        [PreserveSig]
        int GetClassID(out Guid classId);

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Pause();

        [PreserveSig]
        int Run(long start);

        [PreserveSig]
        int GetState(int timeout, out int filterState);

        [PreserveSig]
        int SetSyncSource(IntPtr clock);

        [PreserveSig]
        int GetSyncSource(out IntPtr clock);

        [PreserveSig]
        int EnumPins(out IEnumPins? enumerator);

        [PreserveSig]
        int FindPin([MarshalAs(UnmanagedType.LPWStr)] string id, out IPin pin);

        [PreserveSig]
        int QueryFilterInfo(IntPtr info);

        [PreserveSig]
        int JoinFilterGraph(IntPtr graph, [MarshalAs(UnmanagedType.LPWStr)] string name);

        [PreserveSig]
        int QueryVendorInfo([MarshalAs(UnmanagedType.LPWStr)] out string vendorInfo);
    }

    [ComImport]
    [Guid("56a86892-0ad4-11ce-b03a-0020af0ba770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumPins
    {
        [PreserveSig]
        int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IPin[] pins, IntPtr fetched);
    }

    [ComImport]
    [Guid("56a86891-0ad4-11ce-b03a-0020af0ba770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPin
    {
        [PreserveSig]
        int Connect(IntPtr receivePin, IntPtr mediaType);

        [PreserveSig]
        int ReceiveConnection(IntPtr connector, IntPtr mediaType);

        [PreserveSig]
        int Disconnect();

        [PreserveSig]
        int ConnectedTo(out IntPtr pin);

        [PreserveSig]
        int ConnectionMediaType(IntPtr mediaType);

        [PreserveSig]
        int QueryPinInfo(IntPtr info);

        [PreserveSig]
        int QueryDirection(out int direction);
    }

    [ComImport]
    [Guid("C6E13340-30AC-11d0-A18C-00A0C9118956")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMStreamConfig
    {
        [PreserveSig]
        int SetFormat(IntPtr mediaType);

        [PreserveSig]
        int GetFormat(out IntPtr mediaType);

        [PreserveSig]
        int GetNumberOfCapabilities(out int count, out int size);

        [PreserveSig]
        int GetStreamCaps(int index, out IntPtr mediaType, IntPtr streamConfigCaps);
    }

    [ComImport]
    [Guid("31EFAC30-515C-11d0-A9AA-00AA0061BE93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsPropertySet
    {
        [PreserveSig]
        int Set(ref Guid set, int id, IntPtr instanceData, int instanceLength, IntPtr propertyData, int dataLength);

        [PreserveSig]
        int Get(ref Guid set, int id, IntPtr instanceData, int instanceLength, IntPtr propertyData, int dataLength, out int bytesReturned);

        [PreserveSig]
        int QuerySupported(ref Guid set, int id, out int typeSupport);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AmMediaType
    {
        public Guid MajorType;
        public Guid SubType;
        [MarshalAs(UnmanagedType.Bool)]
        public bool FixedSizeSamples;
        [MarshalAs(UnmanagedType.Bool)]
        public bool TemporalCompression;
        public int SampleSize;
        public Guid FormatType;
        public IntPtr UnkPtr;
        public int FormatSize;
        public IntPtr FormatPtr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoInfoHeader
    {
        public int SrcLeft;
        public int SrcTop;
        public int SrcRight;
        public int SrcBottom;
        public int TargetLeft;
        public int TargetTop;
        public int TargetRight;
        public int TargetBottom;
        public int BitRate;
        public int BitErrorRate;
        public long AvgTimePerFrame;
        public BitmapInfoHeader Bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoInfoHeader2
    {
        public int SrcLeft;
        public int SrcTop;
        public int SrcRight;
        public int SrcBottom;
        public int TargetLeft;
        public int TargetTop;
        public int TargetRight;
        public int TargetBottom;
        public int BitRate;
        public int BitErrorRate;
        public long AvgTimePerFrame;
        public int InterlaceFlags;
        public int CopyProtectFlags;
        public int PictAspectRatioX;
        public int PictAspectRatioY;
        public int ControlFlags;
        public int Reserved2;
        public BitmapInfoHeader Bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoStreamConfigCaps
    {
        public Guid Specifier;
        public uint VideoStandard;
        public OutputSize InputSize;
        public OutputSize MinCroppingSize;
        public OutputSize MaxCroppingSize;
        public int CropGranularityX;
        public int CropGranularityY;
        public int CropAlignX;
        public int CropAlignY;
        public OutputSize MinOutputSize;
        public OutputSize MaxOutputSize;
        public int OutputGranularityX;
        public int OutputGranularityY;
        public int StretchTapsX;
        public int StretchTapsY;
        public int ShrinkTapsX;
        public int ShrinkTapsY;
        public long MinFrameInterval;
        public long MaxFrameInterval;
        public int MinBitsPerSecond;
        public int MaxBitsPerSecond;
    }
}

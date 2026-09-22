using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SwingStudio.Capture;

public static class CameraEnumerator
{
    public static IReadOnlyList<CameraDevice> List()
    {
        var cameras = new List<CameraDevice>();
        var devEnum = (ICreateDevEnum)new CreateDevEnum();
        var category = VideoInputDeviceCategory;
        var hr = devEnum.CreateClassEnumerator(ref category, out var enumerator, 0);
        if (hr != 0 || enumerator is null)
        {
            Marshal.ReleaseComObject(devEnum);
            return cameras;
        }

        CreateBindCtx(0, out var bindContext);
        try
        {
            var fetched = new IMoniker[1];
            var index = 0;
            while (enumerator.Next(1, fetched, IntPtr.Zero) == 0)
            {
                var moniker = fetched[0];
                try
                {
                    moniker.GetDisplayName(bindContext, null!, out var devicePath);
                    var name = ReadFriendlyName(moniker) ?? $"Camera {index + 1}";
                    cameras.Add(new CameraDevice(index, name, devicePath));
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }

                index++;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(bindContext);
            Marshal.ReleaseComObject(enumerator);
            Marshal.ReleaseComObject(devEnum);
        }

        return cameras;
    }

    private static string? ReadFriendlyName(IMoniker moniker)
    {
        try
        {
            var propertyBagId = typeof(IPropertyBag).GUID;
            moniker.BindToStorage(null!, null!, ref propertyBagId, out var storage);
            if (storage is not IPropertyBag bag)
            {
                return null;
            }

            try
            {
                object? value = null;
                if (bag.Read("FriendlyName", ref value, IntPtr.Zero) == 0 && value is string name && name.Length > 0)
                {
                    return name;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(bag);
            }
        }
        catch (COMException)
        {
            return null;
        }

        return null;
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);

    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");

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
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([MarshalAs(UnmanagedType.LPWStr)] string name, ref object? value, IntPtr errorLog);

        [PreserveSig]
        int Write([MarshalAs(UnmanagedType.LPWStr)] string name, ref object value);
    }
}

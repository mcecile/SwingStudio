using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using OpenCvSharp;

namespace SwingStudio.Capture;

public static class CameraControlStore
{
    private const int ControlFlagsAuto = 0x0001;
    private const int ControlFlagsManual = 0x0002;

    private static readonly DialogProperty[] DialogProperties =
    [
        new("Pan", 0, true, VideoCaptureProperties.Pan),
        new("Tilt", 1, true, VideoCaptureProperties.Tilt),
        new("Roll", 2, true, VideoCaptureProperties.Roll),
        new("Zoom", 3, true, VideoCaptureProperties.Zoom),
        new("Exposure", 4, true, VideoCaptureProperties.Exposure),
        new("Iris", 5, true, VideoCaptureProperties.Iris),
        new("Focus", 6, true, VideoCaptureProperties.Focus),
        new("Brightness", 0, false, VideoCaptureProperties.Brightness),
        new("Contrast", 1, false, VideoCaptureProperties.Contrast),
        new("Hue", 2, false, VideoCaptureProperties.Hue),
        new("Saturation", 3, false, VideoCaptureProperties.Saturation),
        new("Sharpness", 4, false, VideoCaptureProperties.Sharpness),
        new("Gamma", 5, false, VideoCaptureProperties.Gamma),
        new("ColorEnable", 6, false, null),
        new("WhiteBalance", 7, false, VideoCaptureProperties.WhiteBalanceBlueU),
        new("BacklightCompensation", 8, false, VideoCaptureProperties.BackLight),
        new("Gain", 9, false, VideoCaptureProperties.Gain)
    ];

    private static readonly Control[] Controls =
    [
        new(VideoCaptureProperties.AutoExposure, true),
        new(VideoCaptureProperties.AutoFocus, true),
        new(VideoCaptureProperties.AutoWB, true),
        new(VideoCaptureProperties.Exposure, true),
        new(VideoCaptureProperties.Gain, true),
        new(VideoCaptureProperties.Brightness, false),
        new(VideoCaptureProperties.Contrast, false),
        new(VideoCaptureProperties.Saturation, false),
        new(VideoCaptureProperties.Hue, false),
        new(VideoCaptureProperties.Sharpness, false),
        new(VideoCaptureProperties.Gamma, false),
        new(VideoCaptureProperties.BackLight, false),
        new(VideoCaptureProperties.WhiteBalanceBlueU, false),
        new(VideoCaptureProperties.Focus, false),
        new(VideoCaptureProperties.Zoom, false)
    ];

    public static void Apply(VideoCapture capture, string? devicePath, IReadOnlyDictionary<string, double> values)
    {
        var handled = ApplyDialog(devicePath, values);
        foreach (var control in Controls)
        {
            if (handled.Contains(control.Property))
            {
                continue;
            }

            if (!values.TryGetValue(control.Property.ToString(), out var value))
            {
                continue;
            }

            if (control.Property == VideoCaptureProperties.AutoExposure && Math.Abs(value - 1) > 0.01 && Math.Abs(value) > 0.01)
            {
                continue;
            }

            capture.Set(control.Property, value);
        }
    }

    public static Dictionary<string, double> Read(VideoCapture capture, string? devicePath)
    {
        var values = new Dictionary<string, double>();
        foreach (var control in Controls)
        {
            var value = capture.Get(control.Property);
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                continue;
            }

            if (control.Property == VideoCaptureProperties.AutoExposure && value < 0)
            {
                continue;
            }

            if (value == 0 && !control.KeepZero)
            {
                continue;
            }

            values[control.Property.ToString()] = value;
        }

        ReadDialog(devicePath, values);
        return values;
    }

    private static void ReadDialog(string? devicePath, Dictionary<string, double> values)
    {
        UseDevice(devicePath, (camera, video) =>
        {
            foreach (var property in DialogProperties)
            {
                if (!TryGet(property, camera, video, out var current, out var flags))
                {
                    continue;
                }

                values[property.Name] = current;
                values[property.Name + "Auto"] = (flags & ControlFlagsAuto) != 0 ? 1 : 0;
            }

            return true;
        });
    }

    private static bool TryGet(DialogProperty property, IAMCameraControl? camera, IAMVideoProcAmp? video, out int value, out int flags)
    {
        if (property.Camera && camera is not null)
        {
            return camera.Get(property.Id, out value, out flags) == 0;
        }

        if (!property.Camera && video is not null)
        {
            return video.Get(property.Id, out value, out flags) == 0;
        }

        value = 0;
        flags = 0;
        return false;
    }

    private static bool TrySet(DialogProperty property, IAMCameraControl? camera, IAMVideoProcAmp? video, int value, int flags)
    {
        if (property.Camera && camera is not null)
        {
            return camera.Set(property.Id, value, flags) == 0;
        }

        if (!property.Camera && video is not null)
        {
            return video.Set(property.Id, value, flags) == 0;
        }

        return false;
    }

    private static HashSet<VideoCaptureProperties> ApplyDialog(string? devicePath, IReadOnlyDictionary<string, double> values)
    {
        var handled = new HashSet<VideoCaptureProperties>();
        UseDevice(devicePath, (camera, video) =>
        {
            foreach (var property in DialogProperties)
            {
                if (!values.TryGetValue(property.Name + "Auto", out var autoValue))
                {
                    continue;
                }

                var current = values.TryGetValue(property.Name, out var stored) ? (int)stored : 0;
                var flags = autoValue >= 0.5 ? ControlFlagsAuto : ControlFlagsManual;
                if (!TrySet(property, camera, video, current, flags))
                {
                    continue;
                }

                if (property.OpenCv is { } openCv)
                {
                    handled.Add(openCv);
                }

                if (property.Name == "Exposure")
                {
                    handled.Add(VideoCaptureProperties.AutoExposure);
                }
                else if (property.Name == "Focus")
                {
                    handled.Add(VideoCaptureProperties.AutoFocus);
                }
                else if (property.Name == "WhiteBalance")
                {
                    handled.Add(VideoCaptureProperties.AutoWB);
                }
            }

            return true;
        });
        return handled;
    }

    private static bool UseDevice(string? devicePath, Func<IAMCameraControl?, IAMVideoProcAmp?, bool> action)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        ICreateDevEnum? devEnum = null;
        IEnumMoniker? enumerator = null;
        IBindCtx? bindContext = null;
        try
        {
            devEnum = (ICreateDevEnum)new CreateDevEnum();
            var category = VideoInputDeviceCategory;
            if (devEnum.CreateClassEnumerator(ref category, out enumerator, 0) != 0 || enumerator is null)
            {
                return false;
            }

            CreateBindCtx(0, out bindContext);
            var fetched = new IMoniker[1];
            while (enumerator.Next(1, fetched, IntPtr.Zero) == 0)
            {
                var moniker = fetched[0];
                object? bound = null;
                try
                {
                    moniker.GetDisplayName(bindContext, null, out var path);
                    if (!string.Equals(path, devicePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var filterId = typeof(IBaseFilter).GUID;
                    moniker.BindToObject(bindContext, null, ref filterId, out bound);
                    return action(bound as IAMCameraControl, bound as IAMVideoProcAmp);
                }
                catch (COMException)
                {
                    return false;
                }
                catch (InvalidCastException)
                {
                    return false;
                }
                finally
                {
                    if (bound is not null)
                    {
                        Marshal.ReleaseComObject(bound);
                    }

                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            if (bindContext is not null)
            {
                Marshal.ReleaseComObject(bindContext);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }

            if (devEnum is not null)
            {
                Marshal.ReleaseComObject(devEnum);
            }
        }

        return false;
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
    [Guid("56a86895-0ad4-11ce-b03a-0020af0ba770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBaseFilter
    {
        void Unused();
    }

    [ComImport]
    [Guid("C6E13370-30AC-11d0-A18C-00A0C9118956")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMCameraControl
    {
        [PreserveSig]
        int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int capsFlags);

        [PreserveSig]
        int Set(int property, int value, int flags);

        [PreserveSig]
        int Get(int property, out int value, out int flags);
    }

    [ComImport]
    [Guid("C6E13360-30AC-11d0-A18C-00A0C9118956")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMVideoProcAmp
    {
        [PreserveSig]
        int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int capsFlags);

        [PreserveSig]
        int Set(int property, int value, int flags);

        [PreserveSig]
        int Get(int property, out int value, out int flags);
    }

    private readonly record struct DialogProperty(string Name, int Id, bool Camera, VideoCaptureProperties? OpenCv);

    private readonly record struct Control(VideoCaptureProperties Property, bool KeepZero);
}

using System.Runtime.InteropServices;

namespace AgenteBiometricoPresencial.Drivers;

internal static class RealScanNative
{
    internal const string DllName = "RS_SDK.dll";
    internal const string DefaultDllPath =
        @"C:\Program Files\Xperix\RealScanSDK\Bin\x64\RS_SDK.dll";

    internal const int Success = 0;
    internal const int ErrorNotSupported = -2;
    internal const int ErrorNoData = -5;
    internal const int ErrorSegmentFewerFingers = -217;
    internal const int ErrorSegmentWrongHand = -218;
    internal const int ErrorNoDevice = -100;
    internal const int ErrorInvalidHandle = -102;
    internal const int ErrorCannotGetUsbDevice = -105;
    internal const int ErrorCannotWriteUsb = -108;
    internal const int ErrorCannotReadUsb = -110;
    internal const int ErrorInvalidDeviceConnection = -124;
    internal const int ErrorDeviceNotInitialized = -127;

    internal const int CaptureFlatTwoFingers = 0x03;
    internal const int CaptureFlatLeftFourFingers = 0x04;
    internal const int CaptureFlatRightFourFingers = 0x05;

    internal const int SlapLeftFour = 1;
    internal const int SlapRightFour = 2;
    internal const int SlapTwoThumbs = 4;

    internal const int LfdLive = 0;
    internal const int LfdFake = 1;
    internal const int LfdOn = 1;

    internal const int TemplateIso19794_2 = 2002;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SlapInfo
    {
        internal int FingerType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        internal Point[] FingerPosition;

        internal int ImageQuality;
        internal int Rotation;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        internal int[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MissingInfo
    {
        internal int FirstFinger;
        internal int SecondFinger;
        internal int ThirdFinger;
        internal int FourthFinger;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct LfdInfo
    {
        internal int Result;
        internal int Score;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct LfdResult
    {
        internal int NumberOfFingers;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        internal LfdInfo[] Fingers;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    internal struct DeviceInfo
    {
        internal int DeviceType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        internal byte[] ProductName;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        internal byte[] DeviceId;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        internal byte[] FirmwareVersion;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        internal byte[] HardwareVersion;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal int[] Reserved;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    internal static extern int RS_InitSDK(byte[] configFileName, int option, ref int numOfDevice);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_InitDevice(int deviceIndex, ref int deviceHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_ExitDevice(int deviceHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_ExitAllDevices();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_GetDeviceInfo(int deviceHandle, ref DeviceInfo deviceInfo);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_SetMinimumFinger(int deviceHandle, int minFingerCount);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_SetCaptureMode(
        int deviceHandle,
        int captureMode,
        int captureOption,
        [MarshalAs(UnmanagedType.I1)] bool withModeLed);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_SetLFDLevel(int deviceHandle, int lfdLevel);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_TakeImageData(
        int deviceHandle,
        int timeout,
        ref IntPtr imageData,
        ref int imageWidth,
        ref int imageHeight);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_AbortCapture(int deviceHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_Segment(
        IntPtr imageData,
        int imageWidth,
        int imageHeight,
        int slapType,
        ref int numberOfFingers,
        ref IntPtr slapInfo,
        ref IntPtr fingerImageData,
        ref IntPtr fingerImageWidths,
        ref IntPtr fingerImageHeights);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_SegmentMissingFinger(
        IntPtr imageData,
        int imageWidth,
        int imageHeight,
        int slapType,
        ref int numberOfFingers,
        ref IntPtr slapInfo,
        ref IntPtr fingerImageData,
        ref IntPtr fingerImageWidths,
        ref IntPtr fingerImageHeights,
        ref MissingInfo missingInfo);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_GetQualityScore(
        IntPtr imageData,
        int imageWidth,
        int imageHeight,
        ref int nistQuality);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_GetLFDResult(int deviceHandle, ref LfdResult lfdResult);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_EncodeWSQ(
        IntPtr pixelData,
        int imageWidth,
        int imageHeight,
        float ratio,
        byte[] wsqBuffer,
        ref int wsqBufferLength);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_GetTemplate(
        int templateType,
        IntPtr imageData,
        int imageWidth,
        int imageHeight,
        byte[] templateBuffer,
        ref int templateSize);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int RS_GetErrStringChar(int errorCode, byte[] errorMessage);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void RS_FreeImageData(IntPtr imageData);
}

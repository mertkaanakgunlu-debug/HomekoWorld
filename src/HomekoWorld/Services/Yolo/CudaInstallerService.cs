using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace HomekoWorld.Services.Yolo;

public static class CudaInstallerService
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string dllToLoad);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    public static bool IsNvidiaGpu()
    {
        var gpuInfo = OrtEpFactory.DetectPrimaryGpu();
        return gpuInfo.hasGpu && (gpuInfo.vendorId == 0x10DE || gpuInfo.name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsMissingLibraries()
    {
        try
        {
            // LoadLibrary ile sistem PATH'inde veya uygulama dizininde DLL'leri arar.
            bool hasTensorRt = CheckLibrary("nvinfer_10.dll") || CheckLibrary("nvinfer_8.dll");
            bool hasCublas   = CheckLibrary("cublas64_12.dll") || CheckLibrary("cublas64_11.dll");
            bool hasCudnn    = CheckLibrary("cudnn64_9.dll") || CheckLibrary("cudnn64_8.dll");

            return !hasTensorRt || !hasCublas || !hasCudnn;
        }
        catch
        {
            return true;
        }
    }

    private static bool CheckLibrary(string libName)
    {
        IntPtr handle = LoadLibrary(libName);
        if (handle != IntPtr.Zero)
        {
            FreeLibrary(handle);
            return true;
        }
        return false;
    }
}

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
            // NOT: TensorRT (nvinfer) proje dışı — CUDA Execution Provider yalnız cuBLAS + cuDNN + cuda runtime ister.
            // Eskiden nvinfer aranıyordu → CUDA tam çalışan NVIDIA sistemlerinde her açılışta yanlış
            // "eksik kütüphane" alarmı + gereksiz indirme penceresi çıkıyordu. (A2)
            // cudart64_12 de eklendi: cublas/cudnn var ama cuda runtime eksikse (kısmi/bozuk indirme) CUDA EP
            // yine çöküp sessizce CPU'ya düşerdi — şimdi bu da indirmeyi tetikler.
            // onnxruntime_providers_cuda.dll installer'a bundle edilir (sürüm-kilitli) → burada aranmaz.
            bool hasCublas = CheckLibrary("cublas64_12.dll") || CheckLibrary("cublas64_11.dll");
            bool hasCudnn  = CheckLibrary("cudnn64_9.dll")   || CheckLibrary("cudnn64_8.dll");
            bool hasCudart = CheckLibrary("cudart64_12.dll") || CheckLibrary("cudart64_11.dll");

            return !hasCublas || !hasCudnn || !hasCudart;
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

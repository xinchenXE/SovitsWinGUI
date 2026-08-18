using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT;

namespace SoVITS_GUI.Services
{
    /// <summary>
    /// 窗口工具类：用于 Unpackaged 模式下获取窗口句柄 (HWND)，以修复 FolderPicker 闪退问题
    /// </summary>
    public static class WindowHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        /// <summary>
        /// 获取当前 WinUI 3 窗口的 HWND，用于初始化 FolderPicker 等需要窗口句柄的操作
        /// </summary>
        public static IntPtr GetWindowHandle(Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            return hwnd;
        }

        /// <summary>
        /// 使用窗口句柄初始化 FolderPicker（Unpackaged 模式必需）
        /// </summary>
        public static void InitializePickerWithWindow(Windows.Storage.Pickers.FolderPicker picker, Window window)
        {
            var hwnd = GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        /// <summary>
        /// 使用窗口句柄初始化 FileOpenPicker（Unpackaged 模式必需）
        /// </summary>
        public static void InitializePickerWithWindow(Windows.Storage.Pickers.FileOpenPicker picker, Window window)
        {
            var hwnd = GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        /// <summary>
        /// 使用窗口句柄初始化 FileSavePicker（Unpackaged 模式必需）
        /// </summary>
        public static void InitializePickerWithWindow(Windows.Storage.Pickers.FileSavePicker picker, Window window)
        {
            var hwnd = GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
    }
}

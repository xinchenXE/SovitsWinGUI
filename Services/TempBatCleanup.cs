using System;
using System.IO;

namespace SoVITS_GUI.Services
{
    /// <summary>
    /// 临时批处理脚本清理：推理 / 高级工具页会生成 .bat 交给 cmd 执行，
    /// 进程退出后应及时删除，避免在 %TEMP% 中无限累积
    /// </summary>
    public static class TempBatCleanup
    {
        /// <summary>
        /// 删除本次使用的临时 bat（尽力而为，忽略失败）
        /// </summary>
        public static void TryDelete(string? batPath)
        {
            if (string.IsNullOrEmpty(batPath)) return;
            try { File.Delete(batPath); } catch { /* 文件可能已被占用或不存在，忽略 */ }
        }

        /// <summary>
        /// 清理 %TEMP% 下指定前缀、超过 1 天的历史残留 bat（异常退出遗留）
        /// </summary>
        public static void PurgeStale(string prefix)
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetTempPath());
                var cutoff = DateTime.Now.AddDays(-1);
                foreach (var file in dir.GetFiles(prefix + "*.bat"))
                {
                    try
                    {
                        if (file.LastWriteTime < cutoff) file.Delete();
                    }
                    catch { /* 单个文件失败忽略 */ }
                }
            }
            catch { /* 整体失败忽略 */ }
        }
    }
}

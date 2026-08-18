using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SoVITS_GUI.Services
{
    /// <summary>
    /// NumPy .npy 格式的最小读写实现。
    /// 仅覆盖本工程用到的场景：一维浮点数组（说话人音色嵌入，256 维 float32），
    /// 用于在 C# 侧直接完成音色混合（加权平均），不再依赖 svc_eva.py 里的硬编码配置
    /// </summary>
    public static class NpyFile
    {
        private static readonly byte[] Magic = { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' };

        /// <summary>
        /// 读取 .npy 文件中的一维浮点数组（支持 float32/float64，小端）
        /// </summary>
        public static double[] ReadVector(string path)
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            // 魔数校验：\x93NUMPY
            var magic = reader.ReadBytes(6);
            if (magic.Length < 6)
                throw new InvalidDataException($"文件过短，不是有效的 .npy 文件: {path}");
            for (int i = 0; i < 6; i++)
            {
                if (magic[i] != Magic[i])
                    throw new InvalidDataException($"魔数不匹配，不是有效的 .npy 文件: {path}");
            }

            var major = reader.ReadByte();
            reader.ReadByte(); // minor 版本号，此处无需使用

            // v1.0 头部长度为 2 字节，v2.0+ 为 4 字节
            int headerLen = major >= 2
                ? (int)reader.ReadUInt32()
                : reader.ReadUInt16();

            var header = Encoding.ASCII.GetString(reader.ReadBytes(headerLen));

            var descrMatch = Regex.Match(header, "'descr':\\s*'([^']+)'");
            var shapeMatch = Regex.Match(header, "'shape':\\s*\\(([^)]*)\\)");
            if (!descrMatch.Success || !shapeMatch.Success)
                throw new InvalidDataException($"无法解析 .npy 头部: {path}");

            var descr = descrMatch.Groups[1].Value;
            var shapeStr = shapeMatch.Groups[1].Value.Trim().TrimEnd(',');
            if (!long.TryParse(shapeStr, out var length) || length < 0)
                throw new InvalidDataException($"仅支持一维数组，无法解析 shape: {path}");

            bool isFloat32 = descr is "<f4" or "=f4" or "f4";
            bool isFloat64 = descr is "<f8" or "=f8" or "f8";
            if (!isFloat32 && !isFloat64)
                throw new InvalidDataException($"不支持的数据类型 {descr}（仅支持 float32/float64）: {path}");

            var result = new double[length];
            if (isFloat32)
            {
                for (long i = 0; i < length; i++) result[i] = reader.ReadSingle();
            }
            else
            {
                for (long i = 0; i < length; i++) result[i] = reader.ReadDouble();
            }
            return result;
        }

        /// <summary>
        /// 把一维数组按 numpy 默认格式（float32、小端、C 序）写入 .npy 文件
        /// </summary>
        public static void WriteVector(string path, IReadOnlyList<double> values)
        {
            // 头部字典与 numpy.save 生成的格式保持一致
            var dict = $"{{'descr': '<f4', 'fortran_order': False, 'shape': ({values.Count},), }}";

            // numpy 要求 magic+版本+长度字段+头部 总长按 64 字节对齐
            const int prefixLen = 6 + 2 + 2;
            int total = prefixLen + dict.Length + 1; // +1 为结尾换行
            int pad = (64 - total % 64) % 64;
            var header = dict + new string(' ', pad) + "\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);

            if (headerBytes.Length > ushort.MaxValue)
                throw new InvalidOperationException("头部长度超出 v1.0 格式上限");

            using var fs = File.Create(path);
            fs.Write(Magic, 0, Magic.Length);
            fs.WriteByte(1); // major 版本
            fs.WriteByte(0); // minor 版本
            fs.WriteByte((byte)(headerBytes.Length & 0xFF));
            fs.WriteByte((byte)((headerBytes.Length >> 8) & 0xFF));
            fs.Write(headerBytes, 0, headerBytes.Length);

            foreach (var v in values)
            {
                var bytes = BitConverter.GetBytes((float)v);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
    }
}

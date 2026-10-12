using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using WinKit.Core.Diagnostics;
using WinKit.Core.Rules;
using WinKit.Core.Serialization;
using WinKit.Core.Text;
using WinKit.Platform.FileSystem;

namespace WinKit.Platform.Cleanup
{
    /// <summary>
    /// 隔离区里的一项。字段与 manifest.jsonl 的一行一一对应，因此必须带 DataContract 特性 ——
    /// Json 基于 DataContractJsonSerializer，没有特性的属性不会被序列化。
    /// </summary>
    [DataContract]
    public sealed class QuarantineItem
    {
        [DataMember(Name = "batch", Order = 0)]
        public string BatchId { get; set; } = string.Empty;

        [DataMember(Name = "original", Order = 1)]
        public string OriginalPath { get; set; } = string.Empty;

        [DataMember(Name = "stored", Order = 2)]
        public string StoredPath { get; set; } = string.Empty;

        [DataMember(Name = "size", Order = 3)]
        public long Size { get; set; }

        [DataMember(Name = "isDir", Order = 4)]
        public bool IsDirectory { get; set; }

        [DataMember(Name = "movedAt", Order = 5)]
        public string MovedAt { get; set; } = string.Empty;
    }

    /// <summary>
    /// 隔离区：清理掉的项先**搬**到这里，而不是删掉。
    ///
    /// 为什么整个产品敢说"可撤销"：隔离就是一次普通的文件移动，撤销就是反向移动一次，
    /// 不需要任何"恢复删除"的黑魔法，也不会像回收站那样受系统策略影响。
    /// 因此这里的每一次搬移都必须同时落一条清单记录，两者缺一不可。
    ///
    /// 存储布局：&lt;Root&gt;\&lt;batchId&gt;\&lt;序号&gt;_&lt;原文件名&gt;，另加一份追加写的
    /// &lt;Root&gt;\manifest.jsonl。序号是必需的：同一批次里出现同名文件太常见
    /// （各浏览器的 Cache\data_0、各软件的 logs\log.txt 之类），没有序号第二次搬移必然撞名。
    /// </summary>
    public sealed class QuarantineStore
    {
        /// <summary>清单文件名。追加写 JSONL，与操作日志同一套"坏行只跳过"的容错策略。</summary>
        public const string ManifestFileName = "manifest.jsonl";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>清单是"谁搬进来了、原来在哪"的唯一真相，读写都要串行化。</summary>
        private readonly object _gate = new object();

        public QuarantineStore(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("隔离区目录不能为空", nameof(rootDirectory));

            Root = PathGuard.Normalize(rootDirectory);
            if (string.IsNullOrEmpty(Root)) throw new ArgumentException("隔离区目录无效", nameof(rootDirectory));

            // 盘符根目录会把整个磁盘变成隔离区，属于明显的配置错误，宁可当场拒绝
            if (Root.Length <= 3) throw new ArgumentException("隔离区不能直接放在盘符根目录：" + Root, nameof(rootDirectory));
        }

        public string Root { get; }

        public string ManifestPath => Path.Combine(Root, ManifestFileName);

        /// <summary>读取清单时被跳过的坏行数量，用于自检（正常应始终为 0）。</summary>
        public int CorruptedLineCount { get; private set; }

        public void EnsureCreated()
        {
            Directory.CreateDirectory(Root);
        }

        /// <summary>
        /// 把 <paramref name="sourcePath"/> 搬进隔离区并登记到清单。
        /// 任何一步失败都直接抛异常，由执行器捕获成"单项失败" —— 这里绝不吞异常，
        /// 否则会出现"文件没了但清单里没有"这种无法撤销的状态。
        /// </summary>
        /// <param name="knownSize">扫描时算出的字节数，直接记进清单，不再重新遍历目录。</param>
        /// <param name="isDirectory">
        /// 调用方扫描时的形态判断。清单里的形态以文件系统实际状态为准，
        /// 因为扫描结果可能已经过期（预览之后目录被换成了同名文件）。
        /// </param>
        public QuarantineItem Move(string sourcePath, string batchId, long knownSize, bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("源路径不能为空", nameof(sourcePath));
            if (string.IsNullOrWhiteSpace(batchId)) throw new ArgumentException("批次号不能为空", nameof(batchId));

            var source = PathGuard.Normalize(sourcePath);

            // 自检：隔离区不能自己搬自己。调用方有责任把 Root 放在清理目标之外（扫描器靠 ExcludeRoots），
            // 但这条断言成本极低，而一旦漏掉就是"整个隔离区连同历史记录一起消失"级的灾难，值得重复一次。
            if (PathGuard.IsUnder(source, Root))
            {
                throw new InvalidOperationException("源路径位于隔离区内部，拒绝搬移：" + source);
            }

            var isDirectoryNow = Directory.Exists(source);
            if (!isDirectoryNow && !File.Exists(source))
            {
                throw new FileNotFoundException("源路径不存在：" + source, source);
            }

            lock (_gate)
            {
                EnsureCreated();

                var batchDirectory = BatchDirectory(batchId);
                Directory.CreateDirectory(batchDirectory);
                var target = AllocateTarget(batchDirectory, source);

                FileOperations.SafeMove(source, target);

                var item = new QuarantineItem
                {
                    BatchId = batchId,
                    OriginalPath = source,
                    StoredPath = target,
                    Size = knownSize,
                    IsDirectory = isDirectoryNow,
                    MovedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                };
                AppendManifest(item);

                Log.Debug("已移入隔离区：" + source + " → " + target);
                return item;
            }
        }

        public int GetItemCount() => ListItems().Count;

        public long GetTotalSize()
        {
            long total = 0;
            foreach (var item in ListItems()) total += item.Size;
            return total;
        }

        public IReadOnlyList<QuarantineItem> ListItems()
        {
            lock (_gate)
            {
                return ReadItems();
            }
        }

        /// <summary>清空整个隔离区（保留 Root 目录本身）。</summary>
        public bool Purge(out long freedBytes, out int failedCount)
        {
            freedBytes = 0;
            failedCount = 0;

            lock (_gate)
            {
                if (!Directory.Exists(Root)) return true;

                List<string> entries;
                try
                {
                    // 先快照再删：边枚举边删在 Windows 上会因为目录已被修改而抛异常
                    entries = new List<string>(Directory.EnumerateFileSystemEntries(Root));
                }
                catch (Exception ex)
                {
                    Log.Warn("无法枚举隔离区：" + ex.Message);
                    failedCount++;
                    return false;
                }

                foreach (var entry in entries)
                {
                    var size = MeasureSize(entry);
                    try
                    {
                        DeleteEntry(entry);
                        freedBytes += size;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Log.Warn("删除隔离项失败：" + entry + " → " + ex.Message);
                    }
                }
            }

            Log.Info("已清空隔离区：释放 " + ByteSize.Format(freedBytes) + "，失败 " + failedCount + " 项");
            return failedCount == 0;
        }

        /// <summary>只清掉指定批次，其余批次与清单记录原样保留。</summary>
        public bool PurgeBatch(string batchId, out long freedBytes, out int failedCount)
        {
            freedBytes = 0;
            failedCount = 0;
            if (string.IsNullOrWhiteSpace(batchId)) return false;

            var kept = new List<QuarantineItem>();

            lock (_gate)
            {
                foreach (var item in ReadItems())
                {
                    if (!string.Equals(item.BatchId, batchId, StringComparison.Ordinal))
                    {
                        kept.Add(item);
                        continue;
                    }

                    var size = MeasureSize(item.StoredPath);
                    try
                    {
                        DeleteEntry(item.StoredPath);
                        freedBytes += size;
                    }
                    catch (Exception ex)
                    {
                        // 删不掉的项必须留在清单里：否则界面上再也看不到这个还占着磁盘的文件
                        failedCount++;
                        kept.Add(item);
                        Log.Warn("删除隔离项失败：" + item.StoredPath + " → " + ex.Message);
                    }
                }

                RewriteManifest(kept);
            }

            TryDeleteBatchDirectory(batchId);
            return failedCount == 0;
        }

        /// <summary>
        /// 读清单。坏行只跳过并计数 —— 一行坏了不代表整个隔离区作废，
        /// 直接抛异常会让用户彻底失去对已搬走文件的追踪。
        /// </summary>
        private List<QuarantineItem> ReadItems()
        {
            var result = new List<QuarantineItem>();
            CorruptedLineCount = 0;

            if (!File.Exists(ManifestPath)) return result;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(ManifestPath, Utf8NoBom);
            }
            catch (Exception ex)
            {
                Log.Warn("读取隔离区清单失败：" + ex.Message);
                return result;
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var item = Json.TryDeserialize<QuarantineItem>(line);
                if (item == null || string.IsNullOrEmpty(item.StoredPath))
                {
                    // 解析失败或缺少存放位置的行都是废记录：没有 StoredPath 就无法管理它
                    CorruptedLineCount++;
                    continue;
                }
                result.Add(item);
            }

            return result;
        }

        private void AppendManifest(QuarantineItem item)
        {
            File.AppendAllText(ManifestPath, Json.Serialize(item) + Environment.NewLine, Utf8NoBom);
        }

        private void RewriteManifest(List<QuarantineItem> items)
        {
            EnsureCreated();

            var sb = new StringBuilder();
            foreach (var item in items) sb.Append(Json.Serialize(item)).Append(Environment.NewLine);

            // 先写临时文件再整体替换：中途失败时旧清单仍然完整，不会出现半截清单
            var temp = ManifestPath + ".tmp";
            File.WriteAllText(temp, sb.ToString(), Utf8NoBom);
            File.Copy(temp, ManifestPath, overwrite: true);
            File.Delete(temp);
        }

        /// <summary>在批次目录里找一个不冲突的名字：&lt;序号&gt;_&lt;原文件名&gt;。</summary>
        private static string AllocateTarget(string batchDirectory, string source)
        {
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = "item";

            for (int sequence = 1; sequence <= 100000; sequence++)
            {
                var candidate = Path.Combine(batchDirectory,
                    sequence.ToString(CultureInfo.InvariantCulture) + "_" + name);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }

            throw new IOException("隔离区内同名序号已用尽：" + name);
        }

        private string BatchDirectory(string batchId) => Path.Combine(Root, Sanitize(batchId));

        private void TryDeleteBatchDirectory(string batchId)
        {
            try
            {
                var directory = BatchDirectory(batchId);
                if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception)
            {
                // 目录残留无害：下次 Purge 会一并清掉，为此报错只会干扰用户
            }
        }

        /// <summary>
        /// 批次号理论上来自 JournalStore.NewBatchId()，但清单和调用方都可能传别的字符串；
        /// 去掉非法字符，防止 "../" 之类把文件搬到隔离区之外。
        /// </summary>
        private static string Sanitize(string text)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            var result = sb.ToString().Trim();
            return result.Length == 0 ? "batch" : result;
        }

        private static void DeleteEntry(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            // 文件已经不在（用户手工删过）视为已释放，不算失败
        }

        /// <summary>尽力而为地算出一项占用的字节数；读不到按 0 计，只影响"释放了多少"的展示。</summary>
        private static long MeasureSize(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (!Directory.Exists(path)) return 0;

                long total = 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; } catch (Exception) { }
                }
                return total;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
